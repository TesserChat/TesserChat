using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TesserChat.Server.Accounts;
using TesserChat.Server.Persistence;
using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests.Accounts;

/// <summary>
/// A booted server host on its own empty database, with scope handling for the account tests.
/// </summary>
/// <remarks>
/// <para>
/// Exists to keep each test to its assertions. Every account test needs the same three steps —
/// create a database, boot the real host so migrations run, then resolve a scoped
/// <see cref="AccountRegistrar"/> per operation — and repeating them inline buried what each test
/// was actually about.
/// </para>
/// <para>
/// A scope per operation, rather than one shared context, is what makes these tests read Postgres
/// instead of a change tracker: a registration and the read that verifies it must not share a
/// <see cref="TesserChatDbContext"/>, or the read would be answered from memory.
/// </para>
/// </remarks>
internal sealed class RegistrarHost : IAsyncDisposable
{
    private readonly TesserChatServerFactory _factory;
    private readonly HttpClient _client;

    private RegistrarHost(TesserChatServerFactory factory, HttpClient client)
    {
        _factory = factory;
        _client = client;
    }

    /// <summary>
    /// Creates an empty database on the shared container and boots a host against it.
    /// </summary>
    /// <param name="postgres">The shared container fixture.</param>
    /// <param name="mode">
    /// Connection mode to configure (§5.2). Omit for the server's default, which is Open.
    /// </param>
    /// <param name="joinSecretHash">Hashed joining password, for a password-gated server.</param>
    /// <param name="allowlist">Permitted public keys, for an allowlist-only server.</param>
    public static async Task<RegistrarHost> StartAsync(
        PostgresFixture postgres,
        string? mode = null,
        string? joinSecretHash = null,
        params string[] allowlist)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var connectionString = await postgres.CreateDatabaseAsync();
        var factory = TesserChatServerFactory.ForDatabase(connectionString);

        if (mode is not null)
        {
            factory = factory.WithConnectionMode(mode, joinSecretHash, allowlist);
        }

        // Boots the host, which is what applies the migrations these tests need.
        var client = factory.CreateClient();

        return new RegistrarHost(factory, client);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> against a registrar in a fresh scope.
    /// </summary>
    public async Task<T> InScopeAsync<T>(Func<AccountRegistrar, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<AccountRegistrar>());
    }

    /// <summary>
    /// Counts the rows in the accounts table, reading through a scope of its own.
    /// </summary>
    public async Task<int> CountAccountsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TesserChatDbContext>();
        return await context.Accounts.CountAsync();
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
