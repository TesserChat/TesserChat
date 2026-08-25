using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TesserChat.Server.Accounts;
using TesserChat.Server.Auth;
using TesserChat.Server.Persistence;
using TesserChat.Server.Setup;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Auth;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Auth;

/// <summary>
/// A booted server on its own database, set up and ready to authenticate (§4.7).
/// </summary>
/// <remarks>
/// Most login tests need a configured server, because the signed payload binds this server's id and
/// that id does not exist until setup writes it. <see cref="StartAsync"/> therefore completes setup
/// by default; the one test that asserts an unconfigured server refuses logins opts out.
/// </remarks>
internal sealed class LoginHost : IAsyncDisposable
{
    private readonly TesserChatServerFactory _factory;
    private readonly HttpClient _client;

    private LoginHost(TesserChatServerFactory factory, HttpClient client, Guid serverId)
    {
        _factory = factory;
        _client = client;
        ServerId = serverId;
    }

    /// <summary>This server's stable id — what a signature must be bound to.</summary>
    /// <remarks><see cref="Guid.Empty"/> when the host was started without setup.</remarks>
    public Guid ServerId { get; }

    /// <summary>Boots a server, completing first-run setup unless told not to.</summary>
    public static async Task<LoginHost> StartAsync(PostgresFixture postgres, bool completeSetup = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var connectionString = await postgres.CreateDatabaseAsync();
        var factory = TesserChatServerFactory.ForDatabase(connectionString);
        var client = factory.CreateClient();

        var serverId = Guid.Empty;
        if (completeSetup)
        {
            // Setup needs an Owner, and that account is incidental to these tests — the identities
            // under test register separately.
            using var founder = IdentityKeyPair.Generate();

            await using var scope = factory.Services.CreateAsyncScope();
            var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
            var result = await setup.CompleteAsync(founder.Public, "Founder", "Test Server");

            Assert.True(result.Succeeded);
            serverId = result.ServerId;
        }

        return new LoginHost(factory, client, serverId);
    }

    /// <summary>Registers an identity so it can log in.</summary>
    public async Task RegisterAsync(IdentityKeyPair identity, string displayName = "Member")
    {
        ArgumentNullException.ThrowIfNull(identity);

        await using var scope = _factory.Services.CreateAsyncScope();
        var registrar = scope.ServiceProvider.GetRequiredService<AccountRegistrar>();
        var result = await registrar.RegisterAsync(identity.Public, displayName);

        Assert.True(result.Succeeded);
    }

    /// <summary>An HTTP client against this server, carrying no credentials.</summary>
    public HttpClient Client => _client;

    /// <summary>Signs a challenge and logs in over HTTP, returning the raw response.</summary>
    /// <remarks>
    /// Goes through the real endpoints rather than the authenticator directly, so what is under test
    /// is what a client actually talks to — including the JSON shape and the status code.
    /// </remarks>
    public async Task<HttpResponseMessage> LoginOverHttpAsync(IdentityKeyPair identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var challenge = await RequestChallengeAsync();
        var signature = LoginChallenge.Sign(identity, challenge.ServerId, challenge.Nonce);

        return await PostLoginAsync(identity.Public.SigningKey.ToArray(), challenge.Nonce, signature);
    }

    /// <summary>Logs in and returns the session token, asserting the login succeeded.</summary>
    public async Task<string> LoginForTokenAsync(IdentityKeyPair identity)
    {
        using var response = await LoginOverHttpAsync(identity);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>Asks for a challenge over HTTP, asserting the server issued one.</summary>
    public async Task<(Guid ServerId, byte[] Nonce)> RequestChallengeAsync()
    {
        using var response = await _client.PostAsync("/auth/challenge", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (
            Guid.Parse(body.GetProperty("serverId").GetString()!),
            Base64Url.DecodeFromChars(body.GetProperty("nonce").GetString()!));
    }

    /// <summary>Presents a signed challenge to the login endpoint.</summary>
    public Task<HttpResponseMessage> PostLoginAsync(
        ReadOnlyMemory<byte> publicKey,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> signature)
        => _client.PostAsJsonAsync("/auth/login", new
        {
            publicKey = Base64Url.EncodeToString(publicKey.Span),
            nonce = Base64Url.EncodeToString(nonce.Span),
            signature = Base64Url.EncodeToString(signature.Span),
        });

    /// <summary>Calls the authenticated session endpoint with <paramref name="token"/>.</summary>
    /// <remarks>
    /// A bearer token in the Authorization header, as a REST client sends it. Passing null omits the
    /// header entirely, which is the unauthenticated case.
    /// </remarks>
    public async Task<HttpResponseMessage> GetSessionAsync(string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/session");

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request);
    }

    /// <summary>Runs an operation against the authenticator in a fresh scope.</summary>
    /// <remarks>
    /// A new scope per call, matching the real request lifetime — the authenticator is scoped to
    /// its DbContext, and reusing one across calls would let a tracked nonce mask a read that
    /// should have gone to the database.
    /// </remarks>
    public async Task<T> AuthAsync<T>(Func<ChallengeAuthenticator, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<ChallengeAuthenticator>());
    }

    /// <summary>Runs an operation directly against the database in a fresh scope.</summary>
    public async Task<T> QueryAsync<T>(Func<TesserChatDbContext, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<TesserChatDbContext>());
    }

    /// <summary>Issues a challenge, asserting the server was able to.</summary>
    public async Task<LoginChallengeIssued> IssueAsync()
    {
        var issued = await AuthAsync(auth => auth.IssueChallengeAsync());
        Assert.NotNull(issued);
        return issued.Value;
    }

    /// <summary>The stored row for a nonce, or null if the server never issued it.</summary>
    public Task<LoginNonce?> FindNonceAsync(byte[] value)
        => QueryAsync(async context => await context.LoginNonces
            .AsNoTracking()
            .SingleOrDefaultAsync(challenge => challenge.Value == value));

    /// <summary>
    /// The token validation rules this server applies, as the bearer scheme receives them.
    /// </summary>
    /// <remarks>
    /// Read from the configured <see cref="JwtBearerOptions"/> rather than rebuilt, so a test
    /// asserting on these is asserting on what actually validates requests.
    /// </remarks>
    public TokenValidationParameters BuildTokenValidationParameters()
    {
        using var scope = _factory.Services.CreateScope();

        return scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters;
    }

    /// <summary>How many signing keys this server has generated.</summary>
    public Task<int> CountSigningKeysAsync()
        => QueryAsync(async context => await context.TokenSigningKeys.CountAsync());

    /// <summary>
    /// Mints a token with this server's own signing key, for cases the issuer will not produce.
    /// </summary>
    /// <remarks>
    /// Signed with the real key so the signature verifies, leaving whatever the test varies —
    /// expiry, usually — as the only thing that can reject it. The key is taken from the store
    /// rather than the table, so a server that has not signed anything yet generates one here
    /// instead of leaving the test with nothing to sign with.
    /// </remarks>
    public async Task<string> MintTokenAsync(
        Guid accountId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        Guid? issuer = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<TokenSigningKeyStore>();
        var key = await store.GetSigningKeyAsync();

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(key.Secret) { KeyId = key.Id.ToString("D") },
            SecurityAlgorithms.HmacSha256);

        // Defaults to this server, so only a test that deliberately varies it gets a foreign issuer.
        var serverId = (issuer ?? ServerId).ToString("D");

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = serverId,
            Audience = serverId,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object> { ["sub"] = accountId.ToString("D") },
        });
    }

    /// <summary>How many challenges the table currently holds.</summary>
    public Task<int> CountNoncesAsync()
        => QueryAsync(async context => await context.LoginNonces.CountAsync());

    /// <summary>
    /// Rewrites a challenge's expiry, to age one without waiting for real time to pass.
    /// </summary>
    /// <remarks>
    /// The alternative is a fake clock, but the authenticator reads the clock in two places and the
    /// property under test is what the database does with an expired row — so moving the row is
    /// both simpler and closer to what actually happens.
    /// </remarks>
    public Task ExpireAsync(byte[] value, DateTimeOffset expiresAt)
        => QueryAsync(async context => await context.LoginNonces
            .Where(challenge => challenge.Value == value)
            .ExecuteUpdateAsync(update => update.SetProperty(challenge => challenge.ExpiresAt, expiresAt)));

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

        // Npgsql pools are keyed by connection string and outlive the factory, so disposing the
        // host alone would leave connections open against a container every other test shares.
        NpgsqlConnection.ClearAllPools();

        return ValueTask.CompletedTask;
    }
}
