using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TesserChat.Server.Accounts;
using TesserChat.Server.Persistence;
using TesserChat.Server.Rooms;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Rooms;

/// <summary>
/// A booted server host on its own empty database, with scope handling for the room tests.
/// </summary>
/// <remarks>
/// A scope per operation, rather than one shared context, is what makes these tests read Postgres
/// instead of a change tracker — a write and the read that verifies it must not share a
/// <see cref="TesserChatDbContext"/>. That matters more here than elsewhere: message ids are
/// assigned by the database, so a test reading them back from a tracked entity would be asserting
/// on EF rather than on what was stored.
/// </remarks>
internal sealed class RoomHost : IAsyncDisposable
{
    private readonly TesserChatServerFactory _factory;
    private readonly HttpClient _client;

    private RoomHost(TesserChatServerFactory factory, HttpClient client)
    {
        _factory = factory;
        _client = client;
    }

    /// <summary>Creates an empty database on the shared container and boots a host against it.</summary>
    public static async Task<RoomHost> StartAsync(PostgresFixture postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var connectionString = await postgres.CreateDatabaseAsync();
        var factory = TesserChatServerFactory.ForDatabase(connectionString);

        // Boots the host, which applies the migrations that create the room tables.
        var client = factory.CreateClient();

        return new RoomHost(factory, client);
    }

    /// <summary>Runs an operation against the room manager in a fresh scope.</summary>
    public async Task<T> RoomsAsync<T>(Func<RoomManager, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _factory.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<RoomManager>());
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
    /// Memberships and authorship carry real foreign keys, so these tests need genuinely registered
    /// accounts rather than arbitrary GUIDs.
    /// </remarks>
    public async Task<Guid> RegisterAccountAsync(string displayName)
    {
        using var identity = IdentityKeyPair.Generate();

        await using var scope = _factory.Services.CreateAsyncScope();
        var registrar = scope.ServiceProvider.GetRequiredService<AccountRegistrar>();

        var result = await registrar.RegisterAsync(identity.Public, displayName);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Account);

        return result.Account.Id;
    }

    /// <summary>Creates a room and returns it, failing the test if creation was refused.</summary>
    public async Task<Room> CreateRoomAsync(string name, Guid? createdBy = null)
    {
        var (result, room) = await RoomsAsync(manager =>
            manager.CreateRoomAsync(name, createdByAccountId: createdBy));

        Assert.True(result.Succeeded);
        Assert.NotNull(room);

        return room;
    }

    /// <summary>Registers an account and joins it to a room, returning the account id.</summary>
    public async Task<Guid> AddMemberAsync(Guid roomId, string displayName)
    {
        var accountId = await RegisterAccountAsync(displayName);

        var result = await RoomsAsync(manager => manager.JoinAsync(roomId, accountId));
        Assert.True(result.Succeeded);

        return accountId;
    }

    /// <summary>Posts a message, failing the test if it was refused.</summary>
    public async Task<RoomMessage> PostAsync(Guid roomId, Guid authorId, string body)
    {
        var (result, message) = await RoomsAsync(manager =>
            manager.PostMessageAsync(roomId, authorId, body));

        Assert.True(result.Succeeded);
        Assert.NotNull(message);

        return message;
    }

    /// <summary>Reads a room's messages straight from the database, oldest first.</summary>
    public async Task<List<RoomMessage>> ReadStoredAsync(Guid roomId)
        => await QueryAsync(async context => await context.RoomMessages
            .AsNoTracking()
            .Where(message => message.RoomId == roomId)
            .OrderBy(message => message.Id)
            .ToListAsync());

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
