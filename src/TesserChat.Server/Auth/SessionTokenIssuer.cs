using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TesserChat.Server.Persistence;

namespace TesserChat.Server.Auth;

/// <summary>
/// Turns a proven identity into a session token (§4.7.6).
/// </summary>
/// <remarks>
/// <para>
/// The token carries <b>one claim: the account id</b>. A JWT is signed, not encrypted — anything in
/// it is readable by every proxy, log, and browser tool that sees the request — so the token says
/// who the caller is and nothing else. A display name would leak a member's chosen name to every
/// hop; roles would leak the server's administrative shape and, worse, would be a snapshot that
/// keeps asserting a permission after it was withdrawn. Roles are resolved per request from the
/// database (§5.3), where a revocation takes effect immediately.
/// </para>
/// <para>
/// <b>The account id is not sensitive to disclose here</b> — it is derived from a public key (§5.1)
/// and is already visible to every member the account talks to. What the token proves is possession,
/// and that comes from the signature, not from the claim being secret.
/// </para>
/// </remarks>
internal sealed class SessionTokenIssuer(
    TokenSigningKeyStore keys,
    TesserChatDbContext context,
    IOptionsMonitor<AuthOptions> options,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Claim naming the account a token authenticates.
    /// </summary>
    /// <remarks>
    /// The standard <c>sub</c> claim rather than a bespoke one, so the token reads correctly in any
    /// JWT tool an operator debugs with.
    /// </remarks>
    public const string AccountClaim = JwtRegisteredClaimNames.Sub;

    /// <summary>
    /// Issues a token for an account whose key possession has already been proven.
    /// </summary>
    /// <remarks>
    /// <b>Proving possession is not this method's job</b> — it trusts <paramref name="accountId"/>
    /// completely, so it must only ever be reached through <see cref="ChallengeAuthenticator"/>
    /// having verified a signature.
    /// </remarks>
    /// <param name="accountId">The account the caller proved they hold the key for.</param>
    public async Task<SessionToken> IssueAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var serverId = await context.ServerInstances
            .AsNoTracking()
            .Select(instance => (Guid?)instance.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "Cannot issue a session token before setup has given this server an identity.");

        var signingKey = await keys.GetSigningKeyAsync(cancellationToken);

        var issuedAt = timeProvider.GetUtcNow();
        var expiresAt = issuedAt + options.CurrentValue.SessionLifetime;

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(signingKey.Secret) { KeyId = signingKey.Id.ToString("D") },
            SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            // Issuer and audience are both this server's id. On a network with no central issuer,
            // "who signed this" and "who is it for" are the same question, and answering it with the
            // id rather than a URL means a server behind a renamed domain still validates its own
            // tokens (§4.7.2).
            Issuer = serverId.ToString("D"),
            Audience = serverId.ToString("D"),
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials,
            Subject = new ClaimsIdentity(
            [
                new Claim(AccountClaim, accountId.ToString("D")),

                // A unique token id, so a future revocation list has something to name. Nothing
                // consumes it yet; it costs 16 bytes and cannot be added retroactively to tokens
                // already in the wild.
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString("D")),
            ]),
        };

        var serialised = new JsonWebTokenHandler().CreateToken(descriptor);

        return new SessionToken(serialised, expiresAt);
    }
}

/// <summary>
/// A session token and when it stops being accepted.
/// </summary>
/// <param name="Value">The encoded JWT, sent as a bearer token.</param>
/// <param name="ExpiresAt">
/// When it expires. Returned so a client can re-authenticate before being refused mid-action rather
/// than discovering expiry from a failed request.
/// </param>
internal readonly record struct SessionToken(string Value, DateTimeOffset ExpiresAt)
{
    /// <summary>Seconds until expiry, as an OAuth-style <c>expires_in</c>.</summary>
    public long SecondsUntilExpiry(DateTimeOffset now)
        => Math.Max(0, (long)(ExpiresAt - now).TotalSeconds);

    /// <summary>The expiry as an ISO-8601 string, for the login response.</summary>
    public string ExpiresAtIso8601()
        => ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
