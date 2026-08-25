using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Identity;
using TesserChat.Shared.Rooms;

namespace TesserChat.Server.Tests.Realtime;

/// <summary>
/// Covers room chat over the hub: posting, subscribing, and reading history (§6, §5.4.1).
/// </summary>
/// <remarks>
/// These go through a real SignalR client against the test server, so what is exercised is an
/// actual invocation over an authenticated connection rather than a hub class called directly. That
/// matters most for the push tests — a hub instance invoked by hand has no group membership, which
/// is the thing being asserted on.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class RoomHubTests(PostgresFixture postgres)
{
    /// <summary>
    /// How long a test waits for a pushed message before calling it lost.
    /// </summary>
    /// <remarks>
    /// Generous, because it is only ever waited out in full by a test that is about to fail — a
    /// push that arrives takes milliseconds. A tight timeout here buys nothing and costs flakes on
    /// a loaded CI runner.
    /// </remarks>
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(10);

    [RequiresDockerFact]
    public async Task AMember_CanPostAndGetTheStoredMessageBack()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);
        await host.RoomsAsync(manager => manager.JoinAsync(room.Id, accountId));

        var connection = await host.ConnectAsync(token);
        var posted = await connection.InvokeAsync<RoomMessageDto>(
            "PostMessage", room.Id, "Hello, room.");

        Assert.Equal("Hello, room.", posted.Body);
        Assert.Equal(room.Id, posted.RoomId);
        Assert.True(posted.Id > 0);

        // The author is the connection's own account, taken from the validated principal.
        Assert.Equal(accountId, posted.AuthorAccountId);

        // And it really is in the room's history, not just echoed back.
        var stored = await host.RoomsAsync(manager => manager.GetHistoryAsync(room.Id));
        Assert.Equal("Hello, room.", Assert.Single(stored.Messages).Body);
    }

    [RequiresDockerFact]
    public async Task TheAuthor_IsTheConnectionsAccountAndNotAParameter()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var mallory = IdentityKeyPair.Generate();
        var (malloryId, token) = await host.RegisterAndLoginAsync(mallory, "Mallory");
        await host.RoomsAsync(manager => manager.JoinAsync(room.Id, malloryId));

        using var victim = IdentityKeyPair.Generate();
        var victimId = await host.RegisterAsync(victim, "Ada");

        var connection = await host.ConnectAsync(token);

        // PostMessage has no sender parameter, so the only way to attempt this is to send one
        // anyway. SignalR refuses the invocation at argument binding, before the method body runs.
        await Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<RoomMessageDto>(
                "PostMessage", room.Id, "Signed, Ada.", victimId));

        // Posting properly is attributed to the caller, never to the account it names.
        var posted = await connection.InvokeAsync<RoomMessageDto>(
            "PostMessage", room.Id, "Signed, Ada.");

        Assert.Equal(malloryId, posted.AuthorAccountId);
        Assert.NotEqual(victimId, posted.AuthorAccountId);
    }

    [RequiresDockerFact]
    public async Task ANonMember_IsRefused()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var identity = IdentityKeyPair.Generate();
        var (_, token) = await host.RegisterAndLoginAsync(identity, "Mallory");

        var connection = await host.ConnectAsync(token);

        var error = await Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<RoomMessageDto>("PostMessage", room.Id, "Let me in."));

        // A HubException specifically, since its message is the one kind SignalR relays — anything
        // else would reach the client as an opaque server fault.
        Assert.Contains("not a member", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await host.RoomsAsync(manager => manager.GetHistoryAsync(room.Id))).Messages);
    }

    [RequiresDockerFact]
    public async Task PostingToARoomThatDoesNotExist_IsRefused()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (_, token) = await host.RegisterAndLoginAsync(identity);
        var connection = await host.ConnectAsync(token);

        await Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<RoomMessageDto>(
                "PostMessage", Guid.NewGuid(), "Into the void."));
    }

    [RequiresDockerFact]
    public async Task AnEmptyMessage_IsRefused()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);
        await host.RoomsAsync(manager => manager.JoinAsync(room.Id, accountId));

        var connection = await host.ConnectAsync(token);

        await Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<RoomMessageDto>("PostMessage", room.Id, "   "));
    }

    [RequiresDockerFact]
    public async Task ASubscriber_ReceivesAMessagePostedByAnother()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var authorIdentity = IdentityKeyPair.Generate();
        var (authorId, authorToken) = await host.RegisterAndLoginAsync(authorIdentity, "Ada");
        await host.RoomsAsync(manager => manager.JoinAsync(room.Id, authorId));

        using var watcherIdentity = IdentityKeyPair.Generate();
        var (_, watcherToken) = await host.RegisterAndLoginAsync(watcherIdentity, "Grace");

        var watcher = await host.ConnectAsync(watcherToken);
        var received = WaitForMessageAsync(watcher);
        await watcher.InvokeAsync("SubscribeToRoom", room.Id);

        var author = await host.ConnectAsync(authorToken);
        await author.InvokeAsync<RoomMessageDto>("PostMessage", room.Id, "Anyone there?");

        var pushed = await received;

        Assert.Equal("Anyone there?", pushed.Body);
        Assert.Equal(authorId, pushed.AuthorAccountId);
        Assert.Equal(room.Id, pushed.RoomId);
    }

    [RequiresDockerFact]
    public async Task TheSender_AlsoReceivesItsOwnMessage()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);
        await host.RoomsAsync(manager => manager.JoinAsync(room.Id, accountId));

        var connection = await host.ConnectAsync(token);
        var received = WaitForMessageAsync(connection);
        await connection.InvokeAsync("SubscribeToRoom", room.Id);

        var returned = await connection.InvokeAsync<RoomMessageDto>(
            "PostMessage", room.Id, "Talking to myself.");

        // The echo is what gives an optimistically-rendered message its real id and timestamp.
        var pushed = await received;
        Assert.Equal(returned.Id, pushed.Id);
        Assert.Equal(returned.PostedAt, pushed.PostedAt);
    }

    [RequiresDockerFact]
    public async Task AMessage_ReachesOnlySubscribersOfItsOwnRoom()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var general = await host.CreateRoomAsync("general");
        var offtopic = await host.CreateRoomAsync("offtopic");

        using var authorIdentity = IdentityKeyPair.Generate();
        var (authorId, authorToken) = await host.RegisterAndLoginAsync(authorIdentity, "Ada");
        await host.RoomsAsync(manager => manager.JoinAsync(general.Id, authorId));

        using var watcherIdentity = IdentityKeyPair.Generate();
        var (_, watcherToken) = await host.RegisterAndLoginAsync(watcherIdentity, "Grace");

        var watcher = await host.ConnectAsync(watcherToken);
        var received = WaitForMessageAsync(watcher);

        // Watching the other room only.
        await watcher.InvokeAsync("SubscribeToRoom", offtopic.Id);

        var author = await host.ConnectAsync(authorToken);
        await author.InvokeAsync<RoomMessageDto>("PostMessage", general.Id, "Only in general.");

        // Groups are per room, so this must not arrive. Waiting the full timeout is the assertion.
        var arrived = await Task.WhenAny(received, Task.Delay(PushTimeout));
        Assert.NotSame(received, arrived);
    }

    [RequiresDockerFact]
    public async Task AnUnsubscribedConnection_StopsReceiving()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var authorIdentity = IdentityKeyPair.Generate();
        var (authorId, authorToken) = await host.RegisterAndLoginAsync(authorIdentity, "Ada");
        await host.RoomsAsync(manager => manager.JoinAsync(room.Id, authorId));

        using var watcherIdentity = IdentityKeyPair.Generate();
        var (_, watcherToken) = await host.RegisterAndLoginAsync(watcherIdentity, "Grace");

        var watcher = await host.ConnectAsync(watcherToken);
        await watcher.InvokeAsync("SubscribeToRoom", room.Id);
        await watcher.InvokeAsync("UnsubscribeFromRoom", room.Id);

        var received = WaitForMessageAsync(watcher);

        var author = await host.ConnectAsync(authorToken);
        await author.InvokeAsync<RoomMessageDto>("PostMessage", room.Id, "After unsubscribing.");

        var arrived = await Task.WhenAny(received, Task.Delay(PushTimeout));
        Assert.NotSame(received, arrived);
    }

    [RequiresDockerFact]
    public async Task SubscribingToARoomThatDoesNotExist_IsRefused()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (_, token) = await host.RegisterAndLoginAsync(identity);
        var connection = await host.ConnectAsync(token);

        // A silent success would leave a client believing it is watching a room that is gone.
        await Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync("SubscribeToRoom", Guid.NewGuid()));
    }

    [RequiresDockerFact]
    public async Task UnsubscribingFromARoomThatDoesNotExist_IsAllowed()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (_, token) = await host.RegisterAndLoginAsync(identity);
        var connection = await host.ConnectAsync(token);

        // What a client does on learning a room was deleted. Refusing would leave it unable to
        // tidy up after exactly the case that needs tidying.
        await connection.InvokeAsync("UnsubscribeFromRoom", Guid.NewGuid());
    }

    [RequiresDockerFact]
    public async Task History_IsReadableOverTheHubByAnAccountThatNeverJoined()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var authorIdentity = IdentityKeyPair.Generate();
        var authorId = await host.RegisterAsync(authorIdentity, "Ada");
        await host.RoomsAsync(manager => manager.JoinAsync(room.Id, authorId));
        await host.RoomsAsync(manager =>
            manager.PostMessageAsync(room.Id, authorId, "Before anyone else arrived."));

        using var readerIdentity = IdentityKeyPair.Generate();
        var (_, readerToken) = await host.RegisterAndLoginAsync(readerIdentity, "Grace");
        var reader = await host.ConnectAsync(readerToken);

        // §5.4.1: joining is not what unlocks a room's past.
        var page = await reader.InvokeAsync<MessagePageDto>(
            "FetchHistory", room.Id, null, 50);

        Assert.Equal("Before anyone else arrived.", Assert.Single(page.Messages).Body);
        Assert.Null(page.NextBefore);
    }

    [RequiresDockerFact]
    public async Task History_PagesBackwardsOverTheHub()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);
        await host.RoomsAsync(manager => manager.JoinAsync(room.Id, accountId));

        var connection = await host.ConnectAsync(token);

        const int total = 7;
        for (var i = 1; i <= total; i++)
        {
            await connection.InvokeAsync<RoomMessageDto>("PostMessage", room.Id, $"message {i}");
        }

        var seen = new List<string>();
        long? cursor = null;

        do
        {
            var page = await connection.InvokeAsync<MessagePageDto>(
                "FetchHistory", room.Id, cursor, 3);

            seen.AddRange(page.Messages.Select(message => message.Body));
            cursor = page.NextBefore;
        }
        while (cursor is not null);

        Assert.Equal(total, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal("message 7", seen[0]);
        Assert.Equal("message 1", seen[^1]);
    }

    [RequiresDockerFact]
    public async Task FetchingHistoryOfARoomThatDoesNotExist_IsRefused()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (_, token) = await host.RegisterAndLoginAsync(identity);
        var connection = await host.ConnectAsync(token);

        // An empty page would be indistinguishable from a room with nothing in it.
        await Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<MessagePageDto>(
                "FetchHistory", Guid.NewGuid(), null, 50));
    }

    [RequiresDockerFact]
    public async Task JoiningAndLeaving_WorkOverTheHub()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var room = await host.CreateRoomAsync("general");

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);
        var connection = await host.ConnectAsync(token);

        await connection.InvokeAsync("JoinRoom", room.Id);
        Assert.True(await host.RoomsAsync(manager => manager.IsMemberAsync(room.Id, accountId)));

        // Membership is what posting needs, so it is now allowed.
        await connection.InvokeAsync<RoomMessageDto>("PostMessage", room.Id, "Just joined.");

        await connection.InvokeAsync("LeaveRoom", room.Id);
        Assert.False(await host.RoomsAsync(manager => manager.IsMemberAsync(room.Id, accountId)));

        await Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<RoomMessageDto>("PostMessage", room.Id, "After leaving."));
    }

    [RequiresDockerFact]
    public async Task JoinedRooms_AreListedForTheCallersOwnAccount()
    {
        await using var host = await HubHost.StartAsync(postgres);
        var general = await host.CreateRoomAsync("general");
        await host.CreateRoomAsync("offtopic");

        using var identity = IdentityKeyPair.Generate();
        var (_, token) = await host.RegisterAndLoginAsync(identity);
        var connection = await host.ConnectAsync(token);

        await connection.InvokeAsync("JoinRoom", general.Id);

        var all = await connection.InvokeAsync<IReadOnlyList<RoomSummary>>("ListRooms");
        var joined = await connection.InvokeAsync<IReadOnlyList<RoomSummary>>("ListJoinedRooms");

        Assert.Equal(["general", "offtopic"], all.Select(room => room.Name));
        Assert.Equal("general", Assert.Single(joined).Name);
    }

    [RequiresDockerFact]
    public async Task AnUnauthenticatedConnection_CannotReachAnyRoomMethod()
    {
        await using var host = await HubHost.StartAsync(postgres);
        await host.CreateRoomAsync("general");

        // Refused at the handshake by [Authorize] on the hub class (§6.2), so there is never a
        // connection on which to attempt a room call in the first place.
        var connection = host.BuildConnection(token: null);

        await Assert.ThrowsAnyAsync<Exception>(async () => await connection.StartAsync());
    }

    /// <summary>
    /// Completes with the next message pushed to <paramref name="connection"/>.
    /// </summary>
    /// <remarks>
    /// Registered before the call that should trigger the push, never after: SignalR delivers to
    /// whatever handlers exist when the message arrives, so a handler attached afterwards is a race
    /// that passes on a slow server and fails on a fast one.
    /// </remarks>
    private static Task<RoomMessageDto> WaitForMessageAsync(HubConnection connection)
    {
        var received = new TaskCompletionSource<RoomMessageDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<RoomMessageDto>(
            nameof(IRoomClient.MessagePosted),
            message => received.TrySetResult(message));

        return received.Task;
    }
}
