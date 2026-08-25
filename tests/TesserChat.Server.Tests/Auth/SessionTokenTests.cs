using System.Buffers.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TesserChat.Server.Auth;
using TesserChat.Server.Persistence;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Auth;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Auth;

/// <summary>
/// Session tokens: issuance, validation, and the boundaries that make them safe (§4.7.6).
/// </summary>
/// <remarks>
/// Against a real host and a real database, because the properties under test live in the
/// interaction between the bearer middleware, the signing key store, and the endpoints — not in any
/// one of them alone.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class SessionTokenTests(PostgresFixture postgres)
{
    [RequiresDockerFact]
    public async Task A_registered_identity_logs_in_and_receives_a_token()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity);

        using var response = await host.LoginOverHttpAsync(identity);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
        Assert.Equal(
            AccountId.FromPublicKey(identity.Public.SigningKey).ToString("D"),
            body.GetProperty("accountId").GetString());

        // Both forms of expiry, so a client with a skewed clock can still schedule around it.
        Assert.True(body.GetProperty("expiresIn").GetInt64() > 0);
        Assert.True(DateTimeOffset.TryParse(
            body.GetProperty("expiresAt").GetString(),
            out _));
    }

    [RequiresDockerFact]
    public async Task A_valid_token_authenticates_a_rest_call()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity);

        var token = await host.LoginForTokenAsync(identity);

        using var response = await host.GetSessionAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            AccountId.FromPublicKey(identity.Public.SigningKey).ToString("D"),
            body.GetProperty("accountId").GetString());
    }

    [RequiresDockerFact]
    public async Task A_request_with_no_token_is_refused()
    {
        await using var host = await LoginHost.StartAsync(postgres);

        using var response = await host.GetSessionAsync(token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task Health_stays_reachable_without_a_token()
    {
        await using var host = await LoginHost.StartAsync(postgres);

        using var response = await host.Client.GetAsync("/health");

        // Adding authentication must not have swept up the liveness probe: a container's
        // HEALTHCHECK carries no credentials (§5.6).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The self-hosted case: tokens from other servers genuinely arrive, and must be refused.
    /// </summary>
    /// <remarks>
    /// Two real servers, each with its own database, its own id, and its own generated signing key —
    /// rather than one server with a substituted key. A token minted by the other server is wrong on
    /// both counts at once, which is exactly what a real one would be.
    /// </remarks>
    [RequiresDockerFact]
    public async Task A_token_from_a_different_server_is_refused()
    {
        await using var issuingServer = await LoginHost.StartAsync(postgres);
        await using var targetServer = await LoginHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();

        // Registered on both, so the only thing separating the two tokens is which server signed.
        await issuingServer.RegisterAsync(identity);
        await targetServer.RegisterAsync(identity);

        var foreignToken = await issuingServer.LoginForTokenAsync(identity);

        using var refused = await targetServer.GetSessionAsync(foreignToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // And the same identity's own token still works there, so the refusal is about the token's
        // origin rather than anything about the account.
        var ownToken = await targetServer.LoginForTokenAsync(identity);
        using var accepted = await targetServer.GetSessionAsync(ownToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    /// <summary>
    /// A foreign token is refused for naming a foreign issuer, not merely for its signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two servers normally differ in both their id and their signing key, so the ordinary
    /// cross-server case is refused by the signature before the issuer is ever considered — which
    /// means it cannot show that issuer validation works. Here the token is signed with the target
    /// server's <b>own</b> key and names the other server as issuer, so the signature verifies and
    /// only the issuer check can reject it.
    /// </para>
    /// <para>
    /// This is not a hypothetical: a self-hoster restoring one server's database onto another, or
    /// cloning a deployment, produces exactly two servers sharing a key. The issuer check is what
    /// keeps their sessions separate.
    /// </para>
    /// </remarks>
    [RequiresDockerFact]
    public async Task A_token_naming_another_server_as_issuer_is_refused()
    {
        await using var issuingServer = await LoginHost.StartAsync(postgres);
        await using var targetServer = await LoginHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        await targetServer.RegisterAsync(identity);

        var accountId = AccountId.FromPublicKey(identity.Public.SigningKey);

        // Signed by the target server, but claiming to have been issued by the other one.
        var foreignIssuer = await targetServer.MintTokenAsync(
            accountId,
            issuedAt: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            issuer: issuingServer.ServerId);

        using var refused = await targetServer.GetSessionAsync(foreignIssuer);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // Identical but for the issuer, and accepted — so the issuer is what was rejected.
        var ownIssuer = await targetServer.MintTokenAsync(
            accountId,
            issuedAt: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        using var accepted = await targetServer.GetSessionAsync(ownIssuer);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [RequiresDockerFact]
    public async Task An_expired_token_is_refused()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity);

        var accountId = AccountId.FromPublicKey(identity.Public.SigningKey);

        // Signed with this server's real key, so the signature verifies and expiry is the only
        // thing left to reject it.
        var expired = await host.MintTokenAsync(
            accountId,
            issuedAt: DateTimeOffset.UtcNow.AddHours(-24),
            expiresAt: DateTimeOffset.UtcNow.AddHours(-12));

        using var response = await host.GetSessionAsync(expired);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A token whose claims were edited after signing.
    /// </summary>
    /// <remarks>
    /// The account id is swapped for another registered account's, which is the attack worth
    /// testing: not a random value, but a real account the attacker wants to be. The signature
    /// covers the payload, so the edit invalidates it.
    /// </remarks>
    [RequiresDockerFact]
    public async Task A_token_with_a_swapped_account_claim_is_refused()
    {
        await using var host = await LoginHost.StartAsync(postgres);

        using var attacker = IdentityKeyPair.Generate();
        using var victim = IdentityKeyPair.Generate();
        await host.RegisterAsync(attacker, "Attacker");
        await host.RegisterAsync(victim, "Victim");

        var token = await host.LoginForTokenAsync(attacker);

        var tampered = SwapSubjectClaim(
            token,
            AccountId.FromPublicKey(victim.Public.SigningKey).ToString("D"));

        using var response = await host.GetSessionAsync(tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A token signed with an attacker-chosen key, naming a key id this server does not hold.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_token_signed_with_an_unknown_key_is_refused()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity);

        var serverId = host.ServerId.ToString("D");
        var forged = MintToken(
            secret: RandomNumberGenerator.GetBytes(TokenSigningKey.SecretSize),
            keyId: Guid.CreateVersion7().ToString("D"),
            issuer: serverId,
            audience: serverId,
            subject: AccountId.FromPublicKey(identity.Public.SigningKey).ToString("D"),
            issuedAt: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        using var response = await host.GetSessionAsync(forged);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// An unsigned token, claiming the <c>none</c> algorithm.
    /// </summary>
    /// <remarks>
    /// The classic JWT attack: strip the signature and set <c>alg</c> to <c>none</c>, hoping the
    /// validator honours the token's own claim about how to check it. Pinning the algorithm is what
    /// refuses this.
    /// </remarks>
    [RequiresDockerFact]
    public async Task An_unsigned_token_is_refused()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity);

        var serverId = host.ServerId.ToString("D");
        var accountId = AccountId.FromPublicKey(identity.Public.SigningKey).ToString("D");

        var header = Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));

        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $$"""{"sub":"{{accountId}}","iss":"{{serverId}}","aud":"{{serverId}}","exp":{{expiry}}}"""));

        // Trailing dot, no signature — the shape an `alg: none` token takes.
        using var response = await host.GetSessionAsync($"{header}.{payload}.");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Only HMAC-SHA256 is accepted, and the rule is stated rather than inferred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted on the validation parameters directly, not through a request. Every token an
    /// attacker could actually mint is refused by something earlier — an unsigned token by the
    /// handler, a differently-keyed one by the signature check — so no HTTP-level test can isolate
    /// this setting. That makes it exactly the kind of defence that can be deleted without a single
    /// test going red, which is the reason to pin it explicitly.
    /// </para>
    /// <para>
    /// What it defends against is algorithm confusion: a validator that honours whatever
    /// <c>alg</c> a token nominates can be steered into verifying with the wrong primitive. The
    /// server signs one way and accepts one way.
    /// </para>
    /// </remarks>
    [RequiresDockerFact]
    public async Task Only_hmac_sha256_is_accepted()
    {
        await using var host = await LoginHost.StartAsync(postgres);

        var parameters = host.BuildTokenValidationParameters();

        Assert.Equal([SecurityAlgorithms.HmacSha256], parameters.ValidAlgorithms);

        // Issuer and audience are enforced by delegates, and a delegate runs whether or not the
        // matching ValidateX flag is set — so the delegates are what this asserts on. Checking the
        // flags instead would be checking something that does not decide anything: with both set to
        // false and the delegates in place, a foreign token is still refused.
        Assert.NotNull(parameters.IssuerValidator);
        Assert.NotNull(parameters.AudienceValidator);

        // These two have no delegate, so here the flag is the control.
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);

        // Well under the library default of five minutes — see AuthOptions.ClockSkew.
        Assert.True(parameters.ClockSkew <= TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// A rejection must not say why it was rejected.
    /// </summary>
    /// <remarks>
    /// The default bearer middleware returns <c>WWW-Authenticate: Bearer error="invalid_token",
    /// error_description="The token expired at ..."</c>, which distinguishes an expired token from a
    /// forged one and leaks the server's clock. §4.7.4 requires one rejection meaning one thing.
    /// </remarks>
    [RequiresDockerFact]
    public async Task A_refused_token_is_not_told_why()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity);

        var accountId = AccountId.FromPublicKey(identity.Public.SigningKey);

        var expired = await host.MintTokenAsync(
            accountId,
            issuedAt: DateTimeOffset.UtcNow.AddHours(-24),
            expiresAt: DateTimeOffset.UtcNow.AddHours(-12));

        using var expiredResponse = await host.GetSessionAsync(expired);
        using var forgedResponse = await host.GetSessionAsync(
            MintToken(
                secret: RandomNumberGenerator.GetBytes(TokenSigningKey.SecretSize),
                keyId: Guid.CreateVersion7().ToString("D"),
                issuer: host.ServerId.ToString("D"),
                audience: host.ServerId.ToString("D"),
                subject: accountId.ToString("D"),
                issuedAt: DateTimeOffset.UtcNow,
                expiresAt: DateTimeOffset.UtcNow.AddHours(1)));

        // Identical rejections: same status, and a challenge header that names no reason.
        Assert.Equal(expiredResponse.StatusCode, forgedResponse.StatusCode);

        var expiredChallenge = expiredResponse.Headers.WwwAuthenticate.ToString();
        var forgedChallenge = forgedResponse.Headers.WwwAuthenticate.ToString();

        Assert.Equal(expiredChallenge, forgedChallenge);
        Assert.DoesNotContain("error_description", expiredChallenge, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", expiredChallenge, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A failed login says nothing about which part failed.
    /// </summary>
    /// <remarks>
    /// The rule from <see cref="LoginStatus"/>: an unregistered key and a bad signature must be
    /// indistinguishable, or login becomes a test for whether a public key is registered here.
    /// </remarks>
    [RequiresDockerFact]
    public async Task Every_failed_login_is_refused_identically()
    {
        await using var host = await LoginHost.StartAsync(postgres);

        using var registered = IdentityKeyPair.Generate();
        using var stranger = IdentityKeyPair.Generate();
        await host.RegisterAsync(registered);

        // A valid signature from a key this server has never seen.
        using var unknownKey = await host.LoginOverHttpAsync(stranger);

        // A registered key, but the signature is another key's over the same challenge.
        var challenge = await host.RequestChallengeAsync();
        var wrongSignature = LoginChallenge.Sign(stranger, challenge.ServerId, challenge.Nonce);
        using var badSignature = await host.PostLoginAsync(
            registered.Public.SigningKey.ToArray(),
            challenge.Nonce,
            wrongSignature);

        // A challenge this server never issued.
        using var unknownNonce = await host.PostLoginAsync(
            registered.Public.SigningKey.ToArray(),
            RandomNumberGenerator.GetBytes(LoginChallenge.NonceSize),
            new byte[IdentityKeyPair.SignatureSize]);

        // Malformed base64 in every field.
        using var malformed = await host.Client.PostAsJsonAsync("/auth/login", new
        {
            publicKey = "not-base64!!",
            nonce = "also-not!!",
            signature = "nope!!",
        });

        HttpResponseMessage[] refusals =
            [unknownKey, badSignature, unknownNonce, malformed];

        Assert.All(refusals, response =>
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));

        var bodies = new List<string>();
        foreach (var response in refusals)
        {
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        // One body, byte for byte, for every way a login can fail.
        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
    }

    [RequiresDockerFact]
    public async Task A_challenge_can_be_requested_without_naming_an_identity()
    {
        await using var host = await LoginHost.StartAsync(postgres);

        var (serverId, nonce) = await host.RequestChallengeAsync();

        // Nothing was sent, so the endpoint cannot be a test for whether a key is registered here
        // (§4.7.3).
        Assert.Equal(host.ServerId, serverId);
        Assert.Equal(LoginChallenge.NonceSize, nonce.Length);
    }

    [RequiresDockerFact]
    public async Task An_unconfigured_server_issues_no_challenge()
    {
        await using var host = await LoginHost.StartAsync(postgres, completeSetup: false);

        using var response = await host.Client.PostAsync("/auth/challenge", content: null);

        // No identity to bind a signature to, so there is no challenge to give (§5.6).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task A_token_carries_only_the_account_id()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity, "Recognisable Display Name");

        var token = await host.LoginForTokenAsync(identity);
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(token);

        // A JWT is signed, not encrypted. Anything in it is readable by every hop that sees the
        // request, so what is absent matters as much as what is present.
        var decoded = string.Join(" ", claims.Claims.Select(claim => claim.Value));

        Assert.DoesNotContain("Recognisable", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Owner", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Member", decoded, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            AccountId.FromPublicKey(identity.Public.SigningKey).ToString("D"),
            claims.Subject);
    }

    /// <summary>
    /// The signing key is generated by the server, not configured.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_signing_key_is_generated_on_first_login()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity);

        // Setup alone signs nothing, so no key exists until a token is issued.
        Assert.Equal(0, await host.CountSigningKeysAsync());

        await host.LoginForTokenAsync(identity);

        Assert.Equal(1, await host.CountSigningKeysAsync());

        // A second login reuses it rather than minting another, or every login would invalidate the
        // one before it.
        await host.LoginForTokenAsync(identity);

        Assert.Equal(1, await host.CountSigningKeysAsync());
    }

    /// <summary>
    /// Two servers never share a signing key, even on the same Postgres instance.
    /// </summary>
    [RequiresDockerFact]
    public async Task Each_server_generates_its_own_signing_key()
    {
        await using var first = await LoginHost.StartAsync(postgres);
        await using var second = await LoginHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        await first.RegisterAsync(identity);
        await second.RegisterAsync(identity);

        await first.LoginForTokenAsync(identity);
        await second.LoginForTokenAsync(identity);

        var firstKey = await first.QueryAsync(async context =>
            await context.TokenSigningKeys.AsNoTracking().SingleAsync());
        var secondKey = await second.QueryAsync(async context =>
            await context.TokenSigningKeys.AsNoTracking().SingleAsync());

        Assert.NotEqual(firstKey.Id, secondKey.Id);
        Assert.NotEqual(firstKey.Secret, secondKey.Secret);
    }

    /// <summary>
    /// A token in the query string does not authenticate a REST call.
    /// </summary>
    /// <remarks>
    /// SignalR needs the query-string form because a WebSocket handshake carries no Authorization
    /// header, but URLs end up in access logs and proxy logs — so it is accepted only for hub paths.
    /// </remarks>
    [RequiresDockerFact]
    public async Task A_token_in_the_query_string_does_not_authenticate_a_rest_call()
    {
        await using var host = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await host.RegisterAsync(identity);

        var token = await host.LoginForTokenAsync(identity);

        using var response = await host.Client.GetAsync(
            $"/auth/session?access_token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Rewrites a token's <c>sub</c> claim without re-signing it.
    /// </summary>
    private static string SwapSubjectClaim(string token, string accountId)
    {
        var parts = token.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Base64Url.DecodeFromChars(parts[1]))!;

        payload["sub"] = JsonSerializer.SerializeToElement(accountId);

        parts[1] = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));

        // The original signature is kept, which is the point: it no longer covers these bytes.
        return string.Join('.', parts);
    }

    /// <summary>Builds a token directly, for cases the issuer would never produce.</summary>
    private static string MintToken(
        byte[] secret,
        string keyId,
        string issuer,
        string audience,
        string subject,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(secret) { KeyId = keyId },
            SecurityAlgorithms.HmacSha256);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object> { ["sub"] = subject },
        });
    }
}
