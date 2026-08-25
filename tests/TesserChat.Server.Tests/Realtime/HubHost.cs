using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TesserChat.Server.Accounts;
using TesserChat.Server.Auth;
using TesserChat.Server.Realtime;
using TesserChat.Server.Setup;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Auth;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Realtime;

/// <summary>
/// A booted server with its hub mapped, and the means to connect to it (§6).
/// </summary>
/// <remarks>
/// Connections go through the real SignalR client against the factory's in-memory test server, so
/// what is under test is an actual handshake — negotiation, authentication, and the lifecycle
/// callbacks — rather than a hub class invoked directly with a hand-built context.
/// </remarks>
internal sealed class HubHost : IAsyncDisposable
{
    private readonly TesserChatServerFactory _factory;
    private readonly HttpClient _client;
    private readonly List<HubConnection> _connections = [];

    private HubHost(TesserChatServerFactory factory, HttpClient client, Guid serverId)
    {
        _factory = factory;
        _client = client;
        ServerId = serverId;
    }

    /// <summary>An HTTP client against this server, carrying no credentials.</summary>
    public HttpClient RawClient => _client;

    /// <summary>This server's stable id.</summary>
    public Guid ServerId { get; }

    /// <summary>The registry the hub records connections in.</summary>
    /// <remarks>
    /// A singleton, so this is the same instance the hub writes to rather than a copy.
    /// </remarks>
    public ConnectionRegistry Registry
        => _factory.Services.GetRequiredService<ConnectionRegistry>();

    /// <summary>Boots a server and completes first-run setup.</summary>
    public static async Task<HubHost> StartAsync(PostgresFixture postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var connectionString = await postgres.CreateDatabaseAsync();
        var factory = TesserChatServerFactory.ForDatabase(connectionString);
        var client = factory.CreateClient();

        using var founder = IdentityKeyPair.Generate();

        await using var scope = factory.Services.CreateAsyncScope();
        var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
        var result = await setup.CompleteAsync(founder.Public, "Founder", "Test Server");

        Assert.True(result.Succeeded);

        return new HubHost(factory, client, result.ServerId);
    }

    /// <summary>Registers an identity and returns the account id it resolves to.</summary>
    public async Task<Guid> RegisterAsync(IdentityKeyPair identity, string displayName = "Member")
    {
        ArgumentNullException.ThrowIfNull(identity);

        await using var scope = _factory.Services.CreateAsyncScope();
        var registrar = scope.ServiceProvider.GetRequiredService<AccountRegistrar>();
        var result = await registrar.RegisterAsync(identity.Public, displayName);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Account);

        return result.Account.Id;
    }

    /// <summary>Registers an identity and logs it in, returning a usable session token.</summary>
    public async Task<(Guid AccountId, string Token)> RegisterAndLoginAsync(
        IdentityKeyPair identity,
        string displayName = "Member")
    {
        var accountId = await RegisterAsync(identity, displayName);
        return (accountId, await LoginForTokenAsync(identity));
    }

    /// <summary>Logs an already-registered identity in over HTTP.</summary>
    /// <remarks>
    /// Through the real login endpoints (§4.7), so the token a connection carries is one a client
    /// could actually have obtained rather than one minted for the test.
    /// </remarks>
    public async Task<string> LoginForTokenAsync(IdentityKeyPair identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        using var challengeResponse = await _client.PostAsync("/auth/challenge", content: null);
        Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);

        var challenge = await challengeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var serverId = Guid.Parse(challenge.GetProperty("serverId").GetString()!);
        var nonce = Base64Url.DecodeFromChars(challenge.GetProperty("nonce").GetString()!);

        var signature = LoginChallenge.Sign(identity, serverId, nonce);

        using var loginResponse = await _client.PostAsJsonAsync("/auth/login", new
        {
            publicKey = Base64Url.EncodeToString(identity.Public.SigningKey),
            nonce = Base64Url.EncodeToString(nonce),
            signature = Base64Url.EncodeToString(signature),
        });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var body = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>
    /// Builds a hub connection carrying <paramref name="token"/>, without starting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport is pointed at the factory's <c>TestServer</c> through both
    /// <c>HttpMessageHandlerFactory</c> and <c>WebSocketFactory</c>, so nothing binds a real port.
    /// </para>
    /// <para>
    /// <paramref name="useQueryString"/> selects how the token is presented. SignalR's
    /// <c>AccessTokenProvider</c> normally puts it in the query string, which is the path §4.7.6
    /// restricts to hub routes; passing <see langword="false"/> sends it as an
    /// <c>Authorization</c> header instead, which is what a long-polling client would do.
    /// </para>
    /// </remarks>
    public HubConnection BuildConnection(string? token, bool useQueryString = true)
    {
        var builder = new HubConnectionBuilder().WithUrl(
            new Uri(_factory.Server.BaseAddress, RealtimeExtensions.HubPath),
            options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.WebSocketFactory = null;

                // Long polling: the test server's websocket support needs a real socket, and the
                // property under test is authentication and lifecycle, which every transport shares.
                options.Transports = HttpTransportType.LongPolling;

                if (token is null)
                {
                    return;
                }

                if (useQueryString)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
                else
                {
                    options.Headers["Authorization"] = $"Bearer {token}";
                }
            });

        var connection = builder.Build();
        _connections.Add(connection);

        return connection;
    }

    /// <summary>Builds and starts a connection, asserting the handshake succeeded.</summary>
    public async Task<HubConnection> ConnectAsync(string? token, bool useQueryString = true)
    {
        var connection = BuildConnection(token, useQueryString);
        await connection.StartAsync();

        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        _connections.Clear();

        _client.Dispose();
        _factory.Dispose();

        NpgsqlConnection.ClearAllPools();
    }

}
