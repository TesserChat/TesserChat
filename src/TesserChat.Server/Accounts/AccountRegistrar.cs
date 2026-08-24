using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Persistence;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Accounts;

/// <summary>
/// Registers public keys as accounts on this server, and resolves them afterwards (§5.1).
/// </summary>
/// <remarks>
/// <para>
/// Registration is idempotent by construction. The account id is derived from the signing key
/// rather than generated, so presenting the same key twice resolves to the same account instead of
/// creating a second one — there is no separate "already registered" path a caller has to
/// remember to take.
/// </para>
/// <para>
/// <b>This does not authenticate anyone.</b> It records that a public key is known to this server;
/// proving possession of the matching private key is the challenge-response flow in §4.7. Nor does
/// it decide <i>whether</i> a key may register — that is the server's connection mode (§5.2, #9),
/// which slots in at the marked point below.
/// </para>
/// </remarks>
internal sealed class AccountRegistrar(TesserChatDbContext context, TimeProvider timeProvider)
{
    /// <summary>
    /// Registers <paramref name="identity"/> on this server, or returns the existing account for it.
    /// </summary>
    /// <param name="identity">The public identity to register. Both public keys are stored.</param>
    /// <param name="displayName">
    /// The name to show other members. Trimmed before storage. Ignored when the key is already
    /// registered — a returning member keeps the name they last set, and re-registering is not a
    /// rename.
    /// </param>
    public async Task<AccountRegistrationResult> RegisterAsync(
        PublicIdentity identity,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!TryNormaliseDisplayName(displayName, out var normalisedDisplayName))
        {
            return AccountRegistrationResult.Rejected(AccountRegistrationStatus.InvalidDisplayName);
        }

        // The connection-mode check (§5.2) belongs here, before anything is written: open,
        // password-gated, or allowlist-only decides whether this key may register at all. Until #9
        // lands every key is admitted, which is the "open" mode's behaviour.

        var existing = await FindAsync(identity.AccountId, cancellationToken);
        if (existing is not null)
        {
            return AccountRegistrationResult.AlreadyRegistered(existing);
        }

        var account = new Account
        {
            Id = identity.AccountId,
            SigningKey = identity.SigningKey.ToArray(),
            EncryptionKey = identity.EncryptionKey.ToArray(),
            DisplayName = normalisedDisplayName,
            RegisteredAt = timeProvider.GetUtcNow(),
        };

        context.Accounts.Add(account);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Detach first: the failed insert is still tracked, and any read through this context
            // would otherwise return the row that was just rejected.
            context.Entry(account).State = EntityState.Detached;

            // Two registrations of the same key may have raced, in which case the unique index did
            // its job and the outcome the caller asked for still holds — report the winner's
            // account rather than surfacing a conflict that resolved itself. Anything else is a
            // real failure and keeps propagating. (The check cannot be a catch filter: C# does not
            // allow await in one.)
            var winner = await FindAsync(identity.AccountId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return AccountRegistrationResult.AlreadyRegistered(winner);
        }

        return AccountRegistrationResult.Created(account);
    }

    /// <summary>
    /// Finds an account by its permanent id, or <see langword="null"/> if it is not registered here.
    /// </summary>
    public async Task<Account?> FindAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await context.Accounts.SingleOrDefaultAsync(account => account.Id == accountId, cancellationToken);

    /// <summary>
    /// Finds the account registered for a raw Ed25519 public key.
    /// </summary>
    /// <remarks>
    /// This is the login lookup (§4.7): a client presents a key, and the server needs the account
    /// to verify the challenge signature against. A wrong-length key resolves to nothing rather
    /// than throwing — it arrives over the wire and is not trusted to be well-formed.
    /// </remarks>
    public async Task<Account?> FindBySigningKeyAsync(
        ReadOnlyMemory<byte> signingKey,
        CancellationToken cancellationToken = default)
    {
        if (signingKey.Length != IdentityKeyPair.PublicKeySize)
        {
            return null;
        }

        return await FindAsync(AccountId.FromPublicKey(signingKey.Span), cancellationToken);
    }

    /// <summary>
    /// Changes an account's display name. The account id is unaffected (§5.1).
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the account does not exist or the name is not acceptable.
    /// </returns>
    public async Task<bool> TrySetDisplayNameAsync(
        Guid accountId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormaliseDisplayName(displayName, out var normalisedDisplayName))
        {
            return false;
        }

        var account = await FindAsync(accountId, cancellationToken);
        if (account is null)
        {
            return false;
        }

        account.DisplayName = normalisedDisplayName;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Whether an account is a member of this server — the check §7.4.1 makes before accepting a
    /// relayed or queued direct message.
    /// </summary>
    /// <remarks>
    /// Membership is presence in this table today. When roles (#8) and kick/ban (§5.5) land, this
    /// is the single place that definition changes, so callers do not each grow their own notion of
    /// what membership means.
    /// </remarks>
    public async Task<bool> IsMemberAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await context.Accounts.AnyAsync(account => account.Id == accountId, cancellationToken);

    /// <summary>
    /// Trims a display name and checks it against the stored bound.
    /// </summary>
    /// <remarks>
    /// Length is measured after trimming, so trailing spaces cannot push an otherwise fine name
    /// over the limit. It is measured in UTF-16 code units, matching what the column stores — a
    /// name of emoji hits the limit sooner than one of ASCII, which is the intended reading of "as
    /// long as the column allows" rather than a defect.
    /// </remarks>
    private static bool TryNormaliseDisplayName(string? displayName, out string normalised)
    {
        normalised = displayName?.Trim() ?? string.Empty;
        return normalised.Length > 0 && normalised.Length <= Account.DisplayNameMaxLength;
    }
}
