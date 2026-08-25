using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TesserChat.Server.Accounts;
using TesserChat.Server.Auditing;
using TesserChat.Server.Authorization;
using TesserChat.Server.Persistence;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Authorization;

/// <summary>
/// A booted server host on its own empty database, with scope handling for the role tests.
/// </summary>
/// <remarks>
/// A scope per operation, rather than one shared context, is what makes these tests read Postgres
/// instead of a change tracker — a mutation and the resolution that verifies it must not share a
/// <see cref="TesserChatDbContext"/>.
/// </remarks>
internal sealed class AuthorizationHost : IAsyncDisposable
{
    private readonly TesserChatServerFactory _factory;
    private readonly HttpClient _client;

    private AuthorizationHost(TesserChatServerFactory factory, HttpClient client)
    {
        _factory = factory;
        _client = client;
    }

    /// <summary>Creates an empty database on the shared container and boots a host against it.</summary>
    public static async Task<AuthorizationHost> StartAsync(PostgresFixture postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var connectionString = await postgres.CreateDatabaseAsync();
        var factory = TesserChatServerFactory.ForDatabase(connectionString);

        // Boots the host, which applies the migrations that seed the default roles.
        var client = factory.CreateClient();

        return new AuthorizationHost(factory, client);
    }

    /// <summary>Runs an operation against the permission resolver in a fresh scope.</summary>
    public async Task<T> ResolveAsync<T>(Func<PermissionResolver, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<PermissionResolver>());
    }

    /// <summary>Runs an operation against the role manager in a fresh scope.</summary>
    public async Task<T> ManageAsync<T>(Func<RoleManager, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<RoleManager>());
    }

    /// <summary>Runs an operation against the audit log in a fresh scope.</summary>
    public async Task<T> AuditAsync<T>(Func<AuditLog, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<AuditLog>());
    }

    /// <summary>Runs an operation directly against the database in a fresh scope.</summary>
    public async Task<T> QueryAsync<T>(Func<TesserChatDbContext, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<TesserChatDbContext>());
    }

    /// <summary>
    /// Registers a fresh identity and returns its account id.
    /// </summary>
    /// <remarks>
    /// Roles attach to accounts, and the foreign key is real, so these tests need genuinely
    /// registered accounts rather than arbitrary GUIDs.
    /// </remarks>
    public async Task<Guid> RegisterAccountAsync(string displayName)
    {
        using var identity = IdentityKeyPair.Generate();

        await using var scope = _factory.Services.CreateAsyncScope();
        var registrar = scope.ServiceProvider.GetRequiredService<AccountRegistrar>();

        var result = await registrar.RegisterAsync(identity.Public, displayName);
        Assert.True(result.Succeeded);

        return result.Account!.Id;
    }

    /// <summary>Finds a seeded role by the name it was seeded with.</summary>
    public async Task<Role> GetRoleAsync(string name)
        => await QueryAsync(async context => await context.Roles.SingleAsync(role => role.Name == name));

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
