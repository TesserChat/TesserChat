using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TesserChat.Server.Accounts;
using TesserChat.Server.Persistence;
using TesserChat.Shared.Auth;

namespace TesserChat.Server.Auth;

/// <summary>
/// Proves a caller holds an identity's private key, without that key ever leaving their device
/// (§4.7).
/// </summary>
/// <remarks>
/// <para>
/// Two steps. <see cref="IssueChallengeAsync"/> hands out a random nonce; <see cref="LoginAsync"/>
/// takes that nonce back with a signature over it and says which account signed. Nothing here
/// issues a session token — that is #13, layered on top of the account id this returns.
/// </para>
/// <para>
/// Three properties make the flow safe, and each is enforced somewhere specific rather than
/// assumed:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Server-scoped.</b> This server's id is inside the signed payload
/// (<see cref="LoginChallenge"/>), so a signature collected by one server verifies nowhere else.
/// </item>
/// <item>
/// <b>Single-use.</b> Consuming a nonce is a conditional UPDATE in Postgres, so two simultaneous
/// presentations cannot both succeed — see <see cref="TryConsumeAsync"/>.
/// </item>
/// <item>
/// <b>Short-lived.</b> An expiry is stamped at issue and checked by that same UPDATE, so the window
/// a captured nonce is interesting for is bounded by the database rather than by a later code path
/// remembering to look.
/// </item>
/// </list>
/// </remarks>
internal sealed class ChallengeAuthenticator(
    TesserChatDbContext context,
    AccountRegistrar accounts,
    IOptionsMonitor<AuthOptions> options,
    TimeProvider timeProvider,
    ILogger<ChallengeAuthenticator> logger)
{
    /// <summary>
    /// Issues a fresh challenge for a client to sign.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately asks for no identity.</b> A caller does not say who they are to get a nonce,
    /// so this cannot be used to test whether a public key is registered here — the same
    /// enumeration boundary the admission gate keeps (§5.2). Who signed is established at
    /// <see cref="LoginAsync"/>, from the signature.
    /// </para>
    /// <para>
    /// The returned server id is what the client binds its signature to. A client that already
    /// knows this server's id should check it matches: a changed id means a different deployment,
    /// not a moved one.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The challenge to sign, or <see langword="null"/> if this server has not been set up and so
    /// has no identity to bind a signature to.
    /// </returns>
    public async Task<LoginChallengeIssued?> IssueChallengeAsync(
        CancellationToken cancellationToken = default)
    {
        var serverId = await GetServerIdAsync(cancellationToken);
        if (serverId is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = now + options.CurrentValue.ChallengeLifetime;
        var value = RandomNumberGenerator.GetBytes(LoginChallenge.NonceSize);

        context.LoginNonces.Add(new LoginNonce
        {
            Value = value,
            IssuedAt = now,
            ExpiresAt = expiresAt,
        });

        await context.SaveChangesAsync(cancellationToken);

        return new LoginChallengeIssued(serverId.Value, value, expiresAt);
    }

    /// <summary>
    /// Verifies a signed challenge and reports which account it authenticates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The nonce is consumed before the signature is checked.</b> Spending it first means a
    /// wrong signature burns the challenge rather than leaving it available to try again — an
    /// attacker gets one attempt per nonce, not unlimited attempts against a nonce that stays valid
    /// until it expires. A legitimate client that fumbles simply asks for another, which costs it
    /// one round trip.
    /// </para>
    /// <para>
    /// The account lookup comes last, after the signature verifies. An unregistered key and a bad
    /// signature therefore do the same work in the same order, so which one it was is not visible
    /// in how long the refusal took.
    /// </para>
    /// </remarks>
    /// <param name="signingPublicKey">The raw Ed25519 public key the caller claims.</param>
    /// <param name="nonce">The challenge this server issued.</param>
    /// <param name="signature">That challenge's payload, signed by the claimed key.</param>
    public async Task<LoginResult> LoginAsync(
        ReadOnlyMemory<byte> signingPublicKey,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken = default)
    {
        var serverId = await GetServerIdAsync(cancellationToken);
        if (serverId is null)
        {
            return LoginResult.Refused(LoginStatus.ServerNotConfigured);
        }

        if (nonce.Length != LoginChallenge.NonceSize)
        {
            // Cannot be one this server issued, since every issued nonce is this length. Reported
            // as unknown: a malformed nonce and an unrecognised one are the same nothing.
            return LoginResult.Refused(LoginStatus.UnknownOrSpentChallenge);
        }

        var consumed = await TryConsumeAsync(nonce, cancellationToken);
        if (consumed != LoginStatus.Authenticated)
        {
            return LoginResult.Refused(consumed);
        }

        if (!LoginChallenge.Verify(signingPublicKey.Span, serverId.Value, nonce.Span, signature.Span))
        {
            return LoginResult.Refused(LoginStatus.InvalidSignature);
        }

        var account = await accounts.FindBySigningKeyAsync(signingPublicKey, cancellationToken);
        if (account is null)
        {
            // A valid signature from a key this server does not know. Not an attack — it is what a
            // client that has not registered yet looks like (§5.2) — so this is information rather
            // than a warning.
            logger.LogInformation(
                "A login presented a valid signature from an unregistered key; registration is "
                + "required before logging in.");

            return LoginResult.Refused(LoginStatus.UnknownAccount);
        }

        return LoginResult.Authenticated(account.Id);
    }

    /// <summary>
    /// Deletes challenges that are past their retention window.
    /// </summary>
    /// <remarks>
    /// A housekeeping backstop, not a correctness requirement: an unswept nonce is still unusable,
    /// because consuming one checks expiry itself rather than trusting the sweep to have removed
    /// it. This only keeps the table from growing without bound on a busy server.
    /// </remarks>
    /// <returns>How many rows were removed.</returns>
    public async Task<int> SweepExpiredChallengesAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow() - options.CurrentValue.ChallengeRetention;

        return await context.LoginNonces
            .Where(challenge => challenge.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Spends a nonce, if it exists, is unspent, and has not expired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One conditional UPDATE, not a read followed by a write.</b> Postgres serialises the
    /// matching rows, so of two requests presenting the same nonce at the same instant exactly one
    /// sees a row affected. A read-then-write would let both pass their check before either wrote,
    /// which is the replay this table exists to prevent — and it would pass every test that did not
    /// run the two concurrently.
    /// </para>
    /// <para>
    /// Expiry is part of that same statement rather than checked around it, so there is no window
    /// in which an expiring nonce is spent by one path and rejected by another.
    /// </para>
    /// </remarks>
    private async Task<LoginStatus> TryConsumeAsync(
        ReadOnlyMemory<byte> nonce,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var value = nonce.ToArray();

        var spent = await context.LoginNonces
            .Where(challenge => challenge.Value == value
                                && challenge.ConsumedAt == null
                                && challenge.ExpiresAt > now)
            .ExecuteUpdateAsync(
                update => update.SetProperty(challenge => challenge.ConsumedAt, now),
                cancellationToken);

        if (spent == 1)
        {
            return LoginStatus.Authenticated;
        }

        // Nothing was spent. Read the row back to say why: expired reads differently from replayed
        // in this server's own log, even though the caller is told neither (see LoginStatus).
        var existing = await context.LoginNonces
            .AsNoTracking()
            .SingleOrDefaultAsync(challenge => challenge.Value == value, cancellationToken);

        if (existing is null)
        {
            return LoginStatus.UnknownOrSpentChallenge;
        }

        if (existing.ConsumedAt is not null)
        {
            // Worth a warning: a challenge this server issued, already spent, presented again.
            // Either a client retrying badly or a captured nonce being replayed, and an operator
            // reading logs should be able to see that it happened.
            logger.LogWarning(
                "A login presented a challenge that was already spent at {ConsumedAt}.",
                existing.ConsumedAt);

            return LoginStatus.UnknownOrSpentChallenge;
        }

        return LoginStatus.ExpiredChallenge;
    }

    /// <summary>
    /// This server's stable id, or <see langword="null"/> while it is unconfigured.
    /// </summary>
    private async Task<Guid?> GetServerIdAsync(CancellationToken cancellationToken)
        => await context.ServerInstances
            .AsNoTracking()
            .Select(instance => (Guid?)instance.Id)
            .SingleOrDefaultAsync(cancellationToken);
}

/// <summary>
/// A challenge handed to a client, with everything it needs to sign correctly.
/// </summary>
/// <param name="ServerId">
/// This server's stable id, which the client binds into its signature (§4.7).
/// </param>
/// <param name="Nonce">The random challenge bytes.</param>
/// <param name="ExpiresAt">When the challenge stops being accepted.</param>
internal readonly record struct LoginChallengeIssued(
    Guid ServerId,
    byte[] Nonce,
    DateTimeOffset ExpiresAt);
