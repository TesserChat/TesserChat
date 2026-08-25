using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TesserChat.Server.Auditing;
using TesserChat.Server.Authorization;
using TesserChat.Server.Persistence;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Setup;

/// <summary>
/// First-run setup: turns an empty database into a server with an Owner (§5.6).
/// </summary>
/// <remarks>
/// <para>
/// Setup is <b>unauthenticated by necessity</b> — there is no Owner yet to authorize it, and the
/// account that will hold Owner does not exist until it runs. Everything below exists to make that
/// safe:
/// </para>
/// <list type="bullet">
/// <item>
/// It runs <b>once</b>. An already-configured server refuses outright, so setup can never be a way
/// to seize Owner on a live server.
/// </item>
/// <item>
/// It is <b>atomic</b>. Account, Owner grant, and server row are written in one transaction, so a
/// crash partway leaves the server unconfigured and retryable rather than half-owned.
/// </item>
/// <item>
/// It can be <b>pinned to a key</b>. With <see cref="SetupOptions.OwnerPublicKey"/> set, only that
/// key can claim Owner, which turns exposing a fresh server from a race into a non-event.
/// </item>
/// </list>
/// </remarks>
internal sealed class SetupService(
    TesserChatDbContext context,
    IOptionsMonitor<SetupOptions> options,
    AuditLog auditLog,
    TimeProvider timeProvider,
    ILogger<SetupService> logger)
{
    /// <summary>Name given to a server whose operator supplied none.</summary>
    private const string FallbackServerName = "TesserChat Server";

    /// <summary>
    /// Whether this server still needs setting up.
    /// </summary>
    /// <remarks>
    /// Setup being complete is defined as the <see cref="ServerInstance"/> row existing — the same
    /// row the completing transaction writes, so there is no second flag that could disagree with
    /// it.
    /// </remarks>
    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default)
        => !await context.ServerInstances.AnyAsync(cancellationToken);

    /// <summary>
    /// Completes setup, registering <paramref name="identity"/> as the server's first Owner.
    /// </summary>
    /// <param name="identity">The public identity claiming Owner.</param>
    /// <param name="ownerDisplayName">The display name for that account.</param>
    /// <param name="serverName">
    /// The server's name. Falls back to <see cref="SetupOptions.ServerName"/>, then to a
    /// placeholder — a server is never nameless.
    /// </param>
    public async Task<SetupResult> CompleteAsync(
        PublicIdentity identity,
        string ownerDisplayName,
        string? serverName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var settings = options.CurrentValue;

        if (!TryNormalise(ownerDisplayName, Account.DisplayNameMaxLength, out var normalisedDisplayName))
        {
            return SetupResult.Refused(SetupStatus.InvalidDisplayName);
        }

        var chosenName = serverName ?? settings.ServerName ?? FallbackServerName;
        if (!TryNormalise(chosenName, ServerInstance.NameMaxLength, out var normalisedServerName))
        {
            return SetupResult.Refused(SetupStatus.InvalidServerName);
        }

        if (!await IsSetupRequiredAsync(cancellationToken))
        {
            return SetupResult.Refused(SetupStatus.AlreadyConfigured);
        }

        if (!IsPinnedOwner(settings.OwnerPublicKey, identity))
        {
            logger.LogWarning(
                "Rejected a setup attempt from {AccountId}: a different public key is pinned as "
                + "{Section}:{Setting}.",
                identity.AccountId,
                SetupOptions.SectionName,
                nameof(SetupOptions.OwnerPublicKey));

            return SetupResult.Refused(SetupStatus.NotThePinnedOwner);
        }

        var now = timeProvider.GetUtcNow();
        var serverId = Guid.NewGuid();

        // One transaction for all three writes. A crash partway through must leave the server
        // unconfigured and retryable, never owned by an account that was not finished being made.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            context.Accounts.Add(new Account
            {
                Id = identity.AccountId,
                SigningKey = identity.SigningKey.ToArray(),
                EncryptionKey = identity.EncryptionKey.ToArray(),
                DisplayName = normalisedDisplayName,
                RegisteredAt = now,
            });

            context.AccountRoles.Add(new AccountRole
            {
                AccountId = identity.AccountId,
                RoleId = SystemRoles.OwnerId,
                GrantedAt = now,
            });

            context.ServerInstances.Add(new ServerInstance
            {
                Id = serverId,
                CreatedAt = now,
                Name = normalisedServerName,
                SetUpAt = now,
                SetUpByAccountId = identity.AccountId,
            });

            // No actor: setup runs before any account exists to attribute it to (§5.5). The
            // account that claimed ownership is the target, so the trail says who the server was
            // handed to and when, which is the first thing worth being able to prove about it.
            auditLog.Record(
                AuditAction.ServerSetUp,
                $"Server '{normalisedServerName}' set up; Owner assigned.",
                actorAccountId: null,
                targetAccountId: identity.AccountId,
                targetRoleId: SystemRoles.OwnerId);

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);

            // Two clients raced on a fresh server and the other one won. The single-row constraint
            // on server_instances is what decided it — the loser reports an already-configured
            // server, which is exactly what it now is.
            if (!await IsSetupRequiredAsync(cancellationToken))
            {
                logger.LogInformation(
                    "A concurrent setup attempt from {AccountId} lost the race; the server is "
                    + "already configured.",
                    identity.AccountId);

                return SetupResult.Refused(SetupStatus.AlreadyConfigured);
            }

            throw;
        }

        logger.LogInformation(
            "Setup complete. Server {ServerName} ({ServerId}) is owned by {AccountId}.",
            normalisedServerName,
            serverId,
            identity.AccountId);

        return SetupResult.Completed(serverId, identity.AccountId);
    }

    /// <summary>
    /// Whether <paramref name="identity"/> is permitted to claim Owner.
    /// </summary>
    /// <remarks>
    /// With no key pinned, anyone may — the documented fallback for bringing a server up on a
    /// machine that is not yet reachable from outside. With one pinned, only that key may, compared
    /// in constant time so a near-miss reveals nothing about the pinned value.
    /// </remarks>
    private bool IsPinnedOwner(string? pinned, PublicIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(pinned))
        {
            return true;
        }

        if (!TryDecodeSigningKey(pinned, out var expected))
        {
            // Fail closed. An operator who pinned a key meant to restrict setup, and a value that
            // cannot be read is not evidence they changed their mind.
            logger.LogError(
                "{Section}:{Setting} could not be read as a public key; refusing all setup "
                + "attempts. Set it to a base64url public key, or remove it.",
                SetupOptions.SectionName,
                nameof(SetupOptions.OwnerPublicKey));

            return false;
        }

        return CryptographicOperations.FixedTimeEquals(identity.SigningKey, expected);
    }

    /// <summary>
    /// Decodes a pinned key, accepting a bare signing key or a full shareable identity token.
    /// </summary>
    private static bool TryDecodeSigningKey(string entry, out byte[] signingKey)
    {
        signingKey = [];

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(entry.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length == IdentityKeyPair.PublicKeySize)
        {
            signingKey = bytes;
            return true;
        }

        if (bytes.Length == IdentityKeyPair.PublicKeySize * 2)
        {
            signingKey = bytes[..IdentityKeyPair.PublicKeySize];
            return true;
        }

        return false;
    }

    private static bool TryNormalise(string? value, int maxLength, out string normalised)
    {
        normalised = value?.Trim() ?? string.Empty;
        return normalised.Length > 0 && normalised.Length <= maxLength;
    }
}
