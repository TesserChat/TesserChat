using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Persistence;
using TesserChat.Server.Rooms;
using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests.Rooms;

/// <summary>
/// Covers posting to a room and reading its history back (§5.4).
/// </summary>
/// <remarks>
/// Paging is where this gets subtle, so it is tested at its boundaries rather than only in the
/// middle: an exactly-full page, a page ending the history, and a cursor walked to the end. Those
/// are the cases where an off-by-one repeats a message or steps over one, and a happy-path test
/// through the middle of a long history will not notice either.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class RoomMessageTests(PostgresFixture postgres)
{
    [RequiresDockerFact]
    public async Task AMember_CanPostToTheirRoom()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        var (result, message) = await server.RoomsAsync(manager =>
            manager.PostMessageAsync(room.Id, member, "Hello, room."));

        Assert.True(result.Succeeded);
        Assert.NotNull(message);
        Assert.Equal("Hello, room.", message.Body);
        Assert.Equal(member, message.AuthorAccountId);
        Assert.Equal(room.Id, message.RoomId);

        // The id is assigned by the database, so a stored message always has one.
        Assert.True(message.Id > 0);
    }

    [RequiresDockerFact]
    public async Task ANonMember_CannotPost()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var outsider = await server.RegisterAccountAsync("Mallory");

        var (result, message) = await server.RoomsAsync(manager =>
            manager.PostMessageAsync(room.Id, outsider, "Let me in."));

        Assert.Equal(RoomMutationStatus.NotAMember, result.Status);
        Assert.Null(message);

        // Refused rather than stored-and-hidden: nothing reached the table.
        Assert.Empty(await server.ReadStoredAsync(room.Id));
    }

    [RequiresDockerFact]
    public async Task AMemberWhoLeft_CanNoLongerPost()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");
        await server.PostAsync(room.Id, member, "While a member.");

        await server.RoomsAsync(manager => manager.LeaveAsync(room.Id, member));

        // Membership is read from the database on every post, so a client still showing the room
        // it has left is refused rather than believed.
        var (result, _) = await server.RoomsAsync(manager =>
            manager.PostMessageAsync(room.Id, member, "After leaving."));

        Assert.Equal(RoomMutationStatus.NotAMember, result.Status);
        Assert.Single(await server.ReadStoredAsync(room.Id));
    }

    [RequiresDockerFact]
    public async Task PostingToARoomThatDoesNotExist_IsRefused()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Ada");

        var (result, _) = await server.RoomsAsync(manager =>
            manager.PostMessageAsync(Guid.NewGuid(), account, "Into the void."));

        Assert.Equal(RoomMutationStatus.NotFound, result.Status);
    }

    [RequiresDockerTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public async Task AnEmptyMessage_IsRefused(string body)
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        var (result, _) = await server.RoomsAsync(manager =>
            manager.PostMessageAsync(room.Id, member, body));

        // An empty message says nothing and would occupy a row in permanent history forever.
        Assert.Equal(RoomMutationStatus.InvalidBody, result.Status);
        Assert.Empty(await server.ReadStoredAsync(room.Id));
    }

    [RequiresDockerFact]
    public async Task AMessage_CannotExceedTheColumnLength()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        var (result, _) = await server.RoomsAsync(manager =>
            manager.PostMessageAsync(room.Id, member, new string('x', RoomMessage.BodyMaxLength + 1)));

        // Refused in code rather than left to Postgres, so the caller gets a status instead of a
        // database exception surfacing from a client's input.
        Assert.Equal(RoomMutationStatus.InvalidBody, result.Status);
    }

    [RequiresDockerFact]
    public async Task AMessageAtExactlyTheColumnLength_IsAccepted()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        var body = new string('x', RoomMessage.BodyMaxLength);
        var (result, message) = await server.RoomsAsync(manager =>
            manager.PostMessageAsync(room.Id, member, body));

        Assert.True(result.Succeeded);
        Assert.NotNull(message);
        Assert.Equal(RoomMessage.BodyMaxLength, message.Body.Length);
    }

    [RequiresDockerFact]
    public async Task AMessageBody_IsStoredAsTypedApartFromSurroundingWhitespace()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        var message = await server.PostAsync(room.Id, member, "  **bold**  and `code`  ");

        // Markdown is the client's business (§9.4) — the server does not rewrite what was said.
        Assert.Equal("**bold**  and `code`", message.Body);
    }

    [RequiresDockerFact]
    public async Task History_IsReadableByAnAccountThatNeverJoined()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");
        await server.PostAsync(room.Id, member, "Posted before anyone else arrived.");

        // §5.4 requires that a member can scroll history from before they joined, so joining
        // cannot be what unlocks a room's past.
        var history = await server.RoomsAsync(manager => manager.GetHistoryAsync(room.Id));

        Assert.Equal("Posted before anyone else arrived.", Assert.Single(history.Messages).Body);
    }

    [RequiresDockerFact]
    public async Task History_ComesBackNewestFirst()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        foreach (var body in new[] { "first", "second", "third" })
        {
            await server.PostAsync(room.Id, member, body);
        }

        var history = await server.RoomsAsync(manager => manager.GetHistoryAsync(room.Id));

        Assert.Equal(["third", "second", "first"], history.Messages.Select(message => message.Body));
        Assert.Null(history.NextBefore);
    }

    [RequiresDockerFact]
    public async Task History_IsScopedToItsOwnRoom()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var general = await server.CreateRoomAsync("general");
        var offtopic = await server.CreateRoomAsync("offtopic");
        var member = await server.RegisterAccountAsync("Ada");

        await server.RoomsAsync(manager => manager.JoinAsync(general.Id, member));
        await server.RoomsAsync(manager => manager.JoinAsync(offtopic.Id, member));

        await server.PostAsync(general.Id, member, "in general");
        await server.PostAsync(offtopic.Id, member, "in offtopic");

        var history = await server.RoomsAsync(manager => manager.GetHistoryAsync(general.Id));

        Assert.Equal("in general", Assert.Single(history.Messages).Body);
    }

    [RequiresDockerFact]
    public async Task AFullPage_ReportsThereIsMoreToRead()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        for (var i = 1; i <= 5; i++)
        {
            await server.PostAsync(room.Id, member, $"message {i}");
        }

        var page = await server.RoomsAsync(manager =>
            manager.GetHistoryAsync(room.Id, pageSize: 2));

        Assert.Equal(["message 5", "message 4"], page.Messages.Select(message => message.Body));
        Assert.Equal(page.Messages[^1].Id, page.NextBefore);
    }

    [RequiresDockerFact]
    public async Task APageEndingTheHistory_ReportsNoCursor()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        for (var i = 1; i <= 4; i++)
        {
            await server.PostAsync(room.Id, member, $"message {i}");
        }

        // Exactly as many messages as the page holds: the page is full, but there is nothing
        // behind it. A pager that inferred "more" from a full page would loop forever here.
        var page = await server.RoomsAsync(manager =>
            manager.GetHistoryAsync(room.Id, pageSize: 4));

        Assert.Equal(4, page.Messages.Count);
        Assert.Null(page.NextBefore);
    }

    [RequiresDockerFact]
    public async Task PagingBackwards_ReachesEveryMessageExactlyOnce()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        const int total = 17;
        for (var i = 1; i <= total; i++)
        {
            await server.PostAsync(room.Id, member, $"message {i}");
        }

        var seen = new List<string>();
        long? cursor = null;

        do
        {
            var before = cursor;
            var page = await server.RoomsAsync(manager =>
                manager.GetHistoryAsync(room.Id, before, pageSize: 5));

            seen.AddRange(page.Messages.Select(message => message.Body));
            cursor = page.NextBefore;
        }
        while (cursor is not null);

        // Neither a repeat nor a gap: the whole history, newest first, walked by cursor.
        Assert.Equal(total, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal("message 17", seen[0]);
        Assert.Equal("message 1", seen[^1]);
    }

    [RequiresDockerFact]
    public async Task APageSizeAboveTheCeiling_IsClamped()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        for (var i = 0; i < RoomManager.MaxPageSize + 5; i++)
        {
            await server.PostAsync(room.Id, member, $"message {i}");
        }

        // A client asking for everything gets a bounded answer, not the room's whole history.
        var page = await server.RoomsAsync(manager =>
            manager.GetHistoryAsync(room.Id, pageSize: int.MaxValue));

        Assert.Equal(RoomManager.MaxPageSize, page.Messages.Count);
        Assert.NotNull(page.NextBefore);
    }

    [RequiresDockerFact]
    public async Task HistoryOfARoomWithNothingInIt_IsEmpty()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");

        var page = await server.RoomsAsync(manager => manager.GetHistoryAsync(room.Id));

        Assert.Empty(page.Messages);
        Assert.Null(page.NextBefore);
    }

    [RequiresDockerFact]
    public async Task MessageIdsIncrease_SoTwoPostedInTheSameInstantStillOrder()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");

        var first = await server.PostAsync(room.Id, member, "first");
        var second = await server.PostAsync(room.Id, member, "second");

        // The reason ordering is by id rather than by timestamp: the sequence separates these two
        // even if the clock does not.
        Assert.True(second.Id > first.Id);
    }

    [RequiresDockerFact]
    public async Task DeletingAnAccountThatHasPosted_IsRefusedRatherThanErasingItsMessages()
    {
        await using var server = await RoomHost.StartAsync(postgres);
        var room = await server.CreateRoomAsync("general");
        var member = await server.AddMemberAsync(room.Id, "Ada");
        await server.PostAsync(room.Id, member, "Part of everyone else's context.");

        // A room's history is a shared record. Cascading here would let one member punch holes in
        // other members' conversations by deleting their account, so Postgres refuses until the
        // caller says what should happen instead — a decision for the kick and ban work.
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await server.QueryAsync(async context =>
            {
                var account = await context.Accounts.SingleAsync(a => a.Id == member);
                context.Accounts.Remove(account);
                return await context.SaveChangesAsync();
            }));

        Assert.Single(await server.ReadStoredAsync(room.Id));
    }
}
