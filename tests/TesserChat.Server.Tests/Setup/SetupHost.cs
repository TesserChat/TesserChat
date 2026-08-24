using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TesserChat.Server.Authorization;
using TesserChat.Server.Persistence;
using TesserChat.Server.Setup;
using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests.Setup;

/// <summary>
/// A booted server host on its own empty database, with scope handling for the setup tests.
/// </summary>
/// <remarks>
/// Setup tests need a database that has been migrated but not set up, which is exactly what a fresh
/// per-test database gives. Some of them boot a <i>second</i> host against the <i>same</i> database
/// to prove that setup state survives a restart, so the connection string is kept reachable.
/// </remarks>
internal sealed class SetupHost : IAsyncDisposable
{
    private readonly TesserChatServerFactory _factory;
    private readonly HttpClient _client;

    private SetupHost(TesserChatServerFactory factory, HttpClient client, string connectionString)
    {
        _factory = factory;
        _client = client;
        ConnectionString = connectionString;
    }

    /// <summary>The database this host is pointed at, for booting a second host against it.</summary>
    public string ConnectionString { get; }

    /// <summary>Creates an empty database and boots an unconfigured server against it.</summary>
    public static async Task<SetupHost> StartAsync(
        PostgresFixture postgres,
        string? pinnedOwnerKey = null,
        string? serverName = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var connectionString = await postgres.CreateDatabaseAsync();
        return await StartAgainstAsync(connectionString, pinnedOwnerKey, serverName);
    }

    /// <summary>
    /// Boots a host against an existing database — a restart, for tests that assert setup state
    /// persists.
    /// </summary>
    public static Task<SetupHost> StartAgainstAsync(
        string connectionString,
        string? pinnedOwnerKey = null,
        string? serverName = null)
    {
        var factory = TesserChatServerFactory.ForDatabase(connectionString);

        if (pinnedOwnerKey is not null || serverName is not null)
        {
            factory = factory.WithSetup(pinnedOwnerKey, serverName);
        }

        var client = factory.CreateClient();

        return Task.FromResult(new SetupHost(factory, client, connectionString));
    }

    /// <summary>Runs an operation against the setup service in a fresh scope.</summary>
    public async Task<T> SetupAsync<T>(Func<SetupService, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<SetupService>());
    }

    /// <summary>Runs an operation against the permission resolver in a fresh scope.</summary>
    public async Task<T> ResolveAsync<T>(Func<PermissionResolver, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<PermissionResolver>());
    }

    /// <summary>Runs an operation directly against the database in a fresh scope.</summary>
    public async Task<T> QueryAsync<T>(Func<TesserChatDbContext, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<TesserChatDbContext>());
    }

    /// <summary>How many accounts hold the Owner role.</summary>
    public Task<int> CountOwnersAsync() => ResolveAsync(resolver => resolver.CountOwnersAsync());

    /// <summary>The server row, or null while the server is unconfigured.</summary>
    public Task<ServerInstance?> GetServerAsync()
        => QueryAsync(async context => await context.ServerInstances.SingleOrDefaultAsync());

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

        // Npgsql pools are keyed by connection string and outlive the factory that created them,
        // so disposing the host alone leaves its connections open against a container every other
        // test shares. Each test has a database of its own, so no other host wants these pools.
        NpgsqlConnection.ClearAllPools();

        return ValueTask.CompletedTask;
    }
}
