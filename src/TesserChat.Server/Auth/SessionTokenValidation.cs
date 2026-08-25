using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TesserChat.Server.Persistence;

namespace TesserChat.Server.Auth;

/// <summary>
/// Builds the rules a session token must satisfy to authenticate a request (§4.7.6).
/// </summary>
/// <remarks>
/// <para>
/// The parameters are assembled once at startup, but everything they depend on is resolved <b>per
/// token</b> rather than baked in. The signing key is resolved through
/// <see cref="TokenSigningKeyStore"/>, so a key generated after startup — or by another instance
/// sharing this database — is usable without a restart. The issuer is resolved through
/// <see cref="ServerIdentityProvider"/> for the same reason: a server completes setup while
/// running, and a value captured at startup would be <see langword="null"/> for the rest of that
/// process's life.
/// </para>
/// </remarks>
internal static class SessionTokenValidation
{
    /// <summary>
    /// The validation rules, resolving keys through <paramref name="services"/> at validation time.
    /// </summary>
    public static TokenValidationParameters Build(IServiceProvider services, TimeSpan clockSkew)
    {
        return new TokenValidationParameters
        {
            // A token is only ever valid on the server that signed it. Both are this server's id
            // (§4.7.6) — on a network of independent deployments, a token from another server is an
            // ordinary thing to receive and must be refused rather than merely failing later.
            ValidateIssuer = true,
            ValidateAudience = true,
            IssuerValidator = (issuer, _, _) => ValidateIssuer(services, issuer),
            AudienceValidator = (audiences, _, _) => IsThisServer(services, audiences),

            ValidateLifetime = true,
            ClockSkew = clockSkew,

            // The property the whole scheme rests on. Anything that turns this off accepts tokens
            // this server never signed.
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, securityToken, keyId, _) =>
                ResolveKey(services, securityToken, keyId),

            // HMAC-SHA256 only. Without pinning this, a token could nominate its own algorithm —
            // the `alg: none` family of attacks, and the RSA-public-key-as-HMAC-secret confusion.
            // The server signs one way, so it accepts one way.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

            // The claim is written as `sub` and must read back as `sub`. The inbound mapping this
            // disables would otherwise rewrite it to a Microsoft-specific URI, so code reading the
            // name it wrote would find nothing.
            NameClaimType = SessionTokenIssuer.AccountClaim,
        };
    }

    /// <summary>
    /// Finds the key a token claims to be signed with.
    /// </summary>
    /// <remarks>
    /// Returning no key fails validation, which is the correct outcome for a token naming a key this
    /// server does not have — including every token issued by a different server.
    /// </remarks>
    private static IEnumerable<SecurityKey> ResolveKey(
        IServiceProvider services,
        SecurityToken securityToken,
        string keyId)
    {
        // A token that names no key, or names something that is not one of this server's key ids,
        // cannot match a key here. Parsing it is not trusting it: what the id selects is only which
        // secret the signature is then checked against.
        if (!Guid.TryParse(keyId, out var id))
        {
            return [];
        }

        var store = services.GetRequiredService<TokenSigningKeyStore>();

        // Synchronous by necessity — the resolver signature is not async. Warm in the common case:
        // the store caches by id, so this only blocks the first time a key is seen in this process.
        var secret = store.FindVerificationKeyAsync(id).GetAwaiter().GetResult();

        return secret is null
            ? []
            : [new SymmetricSecurityKey(secret) { KeyId = keyId }];
    }

    /// <summary>
    /// Accepts an issuer only if it is this server's own id.
    /// </summary>
    /// <exception cref="SecurityTokenInvalidIssuerException">
    /// The token was issued by someone else, or by this server before it had an identity.
    /// </exception>
    private static string ValidateIssuer(IServiceProvider services, string issuer)
    {
        if (!IsThisServer(services, [issuer]))
        {
            // The message reaches this server's logs, not the caller: the endpoint answers a
            // rejected token with a bare 401 (§4.7.4).
            throw new SecurityTokenInvalidIssuerException(
                "The token was not issued by this server.")
            {
                InvalidIssuer = issuer,
            };
        }

        return issuer;
    }

    /// <summary>
    /// Whether any of <paramref name="values"/> is this server's id.
    /// </summary>
    /// <remarks>
    /// An unconfigured server matches nothing: with no identity of its own it cannot have issued
    /// anything, so every token presented to it is someone else's.
    /// </remarks>
    private static bool IsThisServer(IServiceProvider services, IEnumerable<string?> values)
    {
        var serverId = services.GetRequiredService<ServerIdentityProvider>().GetServerId();
        if (serverId is null)
        {
            return false;
        }

        var expected = serverId.Value.ToString("D");

        return values.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
    }
}
