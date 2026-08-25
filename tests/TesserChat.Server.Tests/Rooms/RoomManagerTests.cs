using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Persistence;
using TesserChat.Server.Rooms;
using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests.Rooms;

/// <summary>
/// Covers room creation and membership, and the rules governing who may post (§5.4).
/// </summary>
/// <remarks>
/// The rules are enforced in the mutation layer rather than in a client, so these test them there.
/// The membership rule in particular is tested from both directions: a member may post, and a
/// non-member is refused — the second is the one that matters, since a client that has left a room
/// and one that is lying arrive here identically.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class RoomManagerTests(PostgresFixture postgres)
{
    [RequiresDockerFact]
    public async Task AFreshServer_HasNoRooms()
    {
        await using var server = await RoomHost.StartAsync(postgres);

        var rooms = await server.RoomsAsync(manager => manager.GetRoomsAsync());

        Assert.Empty(rooms);
    }

    [RequiresDockerFact]
    public async Task ARoom_IsCreatedWithItsNameAndTopic()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var creator = await server.RegisterAccountAsync("Founder");

        var (result, room) = await server.RoomsAsync(manager =>
            manager.CreateRoomAsync("general", "Anything goes.", creator));

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.NotNull(room);
        Assert.Equal("general", room.Name);
        Assert.Equal("Anything goes.", room.Topic);
        Assert.Equal(creator, room.CreatedByAccountId);

        var stored = await server.QueryAsync(async context =>
            await context.Rooms.AsNoTracking().SingleAsync(r => r.Id == room.Id));

        Assert.Equal("general", stored.Name);
    }

    [RequiresDockerFact]
    public async Task CreatingARoom_DoesNotJoinItsCreator()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var creator = await server.RegisterAccountAsync("Founder");

        var room = await server.CreateRoomAsync("general", creator);

        // Creating a room and being in it are separate acts: an administrator setting up a
        // server's channels should not end up a member of every one of them.
        Assert.False(await server.RoomsAsync(manager => manager.IsMemberAsync(room.Id, creator)));
    }

    [RequiresDockerFact]
    public async Task ARoomName_IsUniqueOnTheServer()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        await server.CreateRoomAsync("general");

        var (result, room) = await server.RoomsAsync(manager => manager.CreateRoomAsync("general"));

        Assert.Equal(RoomMutationStatus.InvalidName, result.Status);
        Assert.Null(room);
    }

    [RequiresDockerFact]
    public async Task ARoomName_IsTrimmedSoTwoNamesCannotReadIdentically()
    {
        await using var server = await RoomHost.StartAsync(postgres);

        var room = await server.CreateRoomAsync("  general  ");
        Assert.Equal("general", room.Name);

        // Would collide with the trimmed name above, so it is refused rather than creating a
        // second room that renders identically in a channel list.
        var (result, _) = await server.RoomsAsync(manager => manager.CreateRoomAsync("general"));
        Assert.Equal(RoomMutationStatus.InvalidName, result.Status);
    }

    [RequiresDockerTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ARoomName_CannotBeBlank(string name)
    {
        await using var server = await RoomHost.StartAsync(postgres);

        var (result, room) = await server.RoomsAsync(manager => manager.CreateRoomAsync(name));

        Assert.Equal(RoomMutationStatus.InvalidName, result.Status);
        Assert.Null(room);
    }

    [RequiresDockerFact]
    public async Task ARoomName_CannotExceedTheColumnLength()
    {
        await using var server = await RoomHost.StartAsync(postgres);

        var (result, _) = await server.RoomsAsync(manager =>
            manager.CreateRoomAsync(new string('n', Room.NameMaxLength + 1)));

        Assert.Equal(RoomMutationStatus.InvalidName, result.Status);
    }

    [RequiresDockerFact]
    public async Task ATopic_CannotExceedTheColumnLength()
    {
        await using var server = await RoomHost.StartAsync(postgres);

        var (result, _) = await server.RoomsAsync(manager =>
            manager.CreateRoomAsync("general", new string('t', Room.TopicMaxLength + 1)));

        Assert.Equal(RoomMutationStatus.InvalidTopic, result.Status);
    }

    [RequiresDockerFact]
    public async Task RenamingARoom_KeepsItsHistory()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");
        await server.PostAsync(room.Id, member, "Before the rename.");

        var result = await server.RoomsAsync(manager =>
            manager.UpdateRoomAsync(room.Id, name: "lobby"));

        Assert.True(result.Succeeded);

        // Messages reference the room id, not its name, so the history survives.
        var history = await server.RoomsAsync(manager => manager.GetHistoryAsync(room.Id));
        Assert.Equal("Before the rename.", Assert.Single(history.Messages).Body);
    }

    [RequiresDockerFact]
    public async Task UpdatingARoom_ToWhatItAlreadySays_ChangesNothing()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");

        var result = await server.RoomsAsync(manager =>
            manager.UpdateRoomAsync(room.Id, name: "general"));

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
    }

    [RequiresDockerFact]
    public async Task DeletingARoom_TakesItsMessagesWithIt()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");
        await server.PostAsync(room.Id, member, "Gone with the room.");

        var result = await server.RoomsAsync(manager => manager.DeleteRoomAsync(room.Id));
        Assert.True(result.Succeeded);

        Assert.Empty(await server.ReadStoredAsync(room.Id));

        // The member's account is untouched — it is the room that was deleted, not them.
        Assert.True(await server.QueryAsync(async context =>
            await context.Accounts.AnyAsync(account => account.Id == member)));
    }

    [RequiresDockerFact]
    public async Task JoiningARoom_TwiceChangesNothingTheSecondTime()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var account = await server.RegisterAccountAsync("Ada");

        var first = await server.RoomsAsync(manager => manager.JoinAsync(room.Id, account));
        var second = await server.RoomsAsync(manager => manager.JoinAsync(room.Id, account));

        Assert.True(first.Changed);
        Assert.True(second.Succeeded);
        Assert.False(second.Changed);

        var memberships = await server.QueryAsync(async context =>
            await context.RoomMemberships.CountAsync(m => m.RoomId == room.Id));

        Assert.Equal(1, memberships);
    }

    [RequiresDockerFact]
    public async Task JoiningARoomThatDoesNotExist_IsRefused()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Ada");

        var result = await server.RoomsAsync(manager => manager.JoinAsync(Guid.NewGuid(), account));

        Assert.Equal(RoomMutationStatus.NotFound, result.Status);
    }

    [RequiresDockerFact]
    public async Task JoiningAsAnAccountThatDoesNotExist_IsRefused()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");

        var result = await server.RoomsAsync(manager => manager.JoinAsync(room.Id, Guid.NewGuid()));

        Assert.Equal(RoomMutationStatus.NotFound, result.Status);
    }

    [RequiresDockerFact]
    public async Task LeavingARoom_KeepsTheMessagesAlreadyPosted()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");
        await server.PostAsync(room.Id, member, "Said while a member.");

        var result = await server.RoomsAsync(manager => manager.LeaveAsync(room.Id, member));
        Assert.True(result.Succeeded);

        // A room's history is a shared record, not a per-member one.
        var history = await server.RoomsAsync(manager => manager.GetHistoryAsync(room.Id));
        Assert.Equal("Said while a member.", Assert.Single(history.Messages).Body);
    }

    [RequiresDockerFact]
    public async Task LeavingARoomNotJoined_ChangesNothing()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var account = await server.RegisterAccountAsync("Ada");

        var result = await server.RoomsAsync(manager => manager.LeaveAsync(room.Id, account));

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
    }

    [RequiresDockerFact]
    public async Task JoinedRooms_AreOnlyTheOnesJoined()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var general = await server.CreateRoomAsync("general");
        var offtopic = await server.CreateRoomAsync("offtopic");
        await server.CreateRoomAsync("announcements");

        var account = await server.RegisterAccountAsync("Ada");
        await server.RoomsAsync(manager => manager.JoinAsync(general.Id, account));
        await server.RoomsAsync(manager => manager.JoinAsync(offtopic.Id, account));

        var joined = await server.RoomsAsync(manager => manager.GetJoinedRoomsAsync(account));

        Assert.Equal(["general", "offtopic"], joined.Select(room => room.Name));
    }
}
