using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TesserChat.Server.Accounts;
using TesserChat.Server.Auth;
using TesserChat.Server.Persistence;
using TesserChat.Server.Setup;
using TesserChat.Server.Tests.Infrastructure;
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
