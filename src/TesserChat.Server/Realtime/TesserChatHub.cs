using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TesserChat.Server.Auth;
using TesserChat.Server.Rooms;
using TesserChat.Shared.Rooms;

namespace TesserChat.Server.Realtime;

/// <summary>
/// The authenticated real-time connection room chat and presence both ride on (§6).
/// </summary>
/// <remarks>
/// <para>
/// <b>One transport, separable responsibilities.</b> §6 puts room chat and presence (#23) on the
/// same connection, which is a connection-count decision rather than a reason to write them
/// together. The connection lifecycle is this class's own: it authenticates a connection, resolves
/// it to an account, and records it in <see cref="ConnectionRegistry"/>.
/// </para>
/// <para>
/// <b>Room chat fans out through SignalR groups, not through the registry.</b> One group per room,
/// named <c>room:{id}</c>, so the server pushes to "everyone watching this room" without keeping a
/// second record of who that is. Presence reads <see cref="ConnectionRegistry"/> and room chat does
/// not, which is what keeps the two separable on one connection (§6.1) — and it is the same
/// mechanism §8.2 already specifies for presence, rather than a second one invented here.
/// </para>
/// <para>
/// <b>The hub decides nothing about rooms.</b> Membership, validation, and storage are
/// <see cref="RoomManager"/>'s (§5.4.1); these methods resolve the caller's account, call it, and
/// turn a refusal into a fault the client can read. A rule enforced here as well as there would be
/// a rule with two places to drift.
/// </para>
/// <para>
/// <b>Authenticated for the whole hub, not per method.</b> <see cref="AuthorizeAttribute"/> here
/// applies to the connection itself, so an unauthenticated client is refused at the handshake and
/// never reaches a hub method. Putting it on individual methods instead would let a connection
/// establish first and be rejected only when it called something — which is exactly the state
/// presence would then have to decide what to do about.
/// </para>
/// </remarks>
[Authorize]
internal sealed class TesserChatHub(
    ConnectionRegistry connections,
    RoomManager rooms,
    ILogger<TesserChatHub> logger) : Hub<IRoomClient>
{
    /// <summary>
    /// Records the connection against the account its token proved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The account comes from the validated principal, never from the client.</b> By the time
    /// this runs the bearer token has already been checked (§4.7.6), so the claim is this server's
    /// own statement about who connected. A client-supplied identifier at this point would be an
    /// unauthenticated claim wearing an authenticated connection's clothes.
    /// </para>
    /// <para>
    /// A principal that carries no usable account id aborts the connection. That should be
    /// unreachable — the token was validated, and this server only ever signs tokens carrying one —
    /// so it means this server issued something malformed, and refusing is better than proceeding
    /// with a connection belonging to nobody.
    /// </para>
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        var accountId = Context.User?.GetAccountId();

        if (accountId is null)
        {
            logger.LogWarning(
                "A hub connection authenticated but carried no account id; refusing it.");

            Context.Abort();
            return;
        }

        var cameOnline = connections.Add(Context.ConnectionId, accountId.Value);

        logger.LogDebug(
            "Account {AccountId} connected as {ConnectionId}; first connection: {CameOnline}.",
            accountId.Value,
            Context.ConnectionId,
            cameOnline);

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Forgets the connection, taking its account offline if it was the last one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Runs on every disconnect, clean or not.</b> SignalR calls this for a client that closed
    /// politely and for one whose network vanished, with <paramref name="exception"/> being the only
    /// difference. Presence depends on that being true — a registry that only cleaned up after
    /// polite disconnects would show dropped clients online forever.
    /// </para>
    /// <para>
    /// Removal is unconditional and tolerates an unknown connection, so a connect that aborted
    /// before it registered still leaves nothing behind.
    /// </para>
    /// </remarks>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var accountId = connections.FindAccount(Context.ConnectionId);
        var wentOffline = connections.Remove(Context.ConnectionId);

        if (exception is not null)
        {
            // Information rather than a warning: a client losing its network is ordinary, and §8.2
            // has clients reconnecting with backoff as normal operation.
            logger.LogInformation(
                exception,
                "Connection {ConnectionId} dropped without closing cleanly.",
                Context.ConnectionId);
        }

        logger.LogDebug(
            "Connection {ConnectionId} for account {AccountId} closed; last connection: {WentOffline}.",
            Context.ConnectionId,
            accountId,
            wentOffline);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// The account id this connection authenticated as.
    /// </summary>
    /// <remarks>
    /// The smallest possible thing a client can call, and it exists for the same reason
    /// <c>GET /auth/session</c> does: a client needs one call that proves the connection works and
    /// resolved to the right identity, without waiting for a feature to be built on top. It tells
    /// the caller only what they already proved by connecting.
    /// </remarks>
    public Guid WhoAmI() => RequireAccount();

    /// <summary>
    /// Subscribes this connection to a room's live messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Subscribing is not joining.</b> Membership is a stored fact about the account (§5.4.1);
    /// this is a fact about one connection, and it lasts only as long as that connection does.
    /// SignalR drops a connection's group memberships when it disconnects, so a reconnecting client
    /// re-subscribes to the rooms it is showing — which is correct, because a connection that has
    /// gone away should not be counted as watching anything.
    /// </para>
    /// <para>
    /// <b>Membership is not required to subscribe</b>, for the same reason it is not required to
    /// read history (§5.4.1): a member browsing a room they have not joined should see it live
    /// rather than a frozen snapshot they must refresh. Posting is what membership gates.
    /// </para>
    /// </remarks>
    public async Task SubscribeToRoom(Guid roomId)
    {
        await RequireRoomAsync(roomId);

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId));
    }

    /// <summary>Stops this connection receiving a room's live messages.</summary>
    /// <remarks>
    /// Does not check the room exists: unsubscribing from something already gone is exactly what a
    /// client does when it learns a room was deleted, and refusing that would leave it unable to
    /// tidy up.
    /// </remarks>
    public async Task UnsubscribeFromRoom(Guid roomId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId));

    /// <summary>
    /// Posts a message to a room and pushes it to everyone watching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The author is the connection's account, not a parameter.</b> There is no sender argument
    /// here by design: the account was proven at the handshake (§6.2), and accepting one from the
    /// caller would be an unauthenticated claim on an authenticated connection. The same goes for
    /// the timestamp, which <see cref="RoomManager"/> takes from the server's clock.
    /// </para>
    /// <para>
    /// <b>Storage decides, then delivery follows.</b> The message is written first and pushed only
    /// if the write succeeded, so nothing is ever delivered that is not in the room's history. The
    /// reverse order would show members a message that a failed write then lost.
    /// </para>
    /// <para>
    /// The push goes to the room's group, which includes the sender's own connection — that is how
    /// the client learns the server-assigned id and timestamp for what it just sent.
    /// </para>
    /// </remarks>
    public async Task<RoomMessageDto> PostMessage(Guid roomId, string body)
    {
        var accountId = RequireAccount();

        var (result, message) = await rooms.PostMessageAsync(roomId, accountId, body);

        if (!result.Succeeded || message is null)
        {
            throw Refuse(result.Status);
        }

        var dto = message.ToDto();
        await Clients.Group(RoomGroup(roomId)).MessagePosted(dto);

        return dto;
    }

    /// <summary>
    /// Reads a page of a room's history, newest first.
    /// </summary>
    /// <param name="roomId">The room to read.</param>
    /// <param name="before">
    /// Return messages older than this id, or <see langword="null"/> to start at the newest.
    /// </param>
    /// <param name="pageSize">
    /// How many to return. Clamped by <see cref="RoomManager.MaxPageSize"/>, so asking for
    /// everything yields a bounded answer rather than the room's entire history.
    /// </param>
    /// <remarks>
    /// Available to any authenticated connection, membership or not — §5.4.1 requires that a member
    /// can scroll history from before they joined, so joining cannot be what unlocks a room's past.
    /// </remarks>
    public async Task<MessagePageDto> FetchHistory(
        Guid roomId,
        long? before = null,
        int pageSize = RoomManager.DefaultPageSize)
    {
        await RequireRoomAsync(roomId);

        var page = await rooms.GetHistoryAsync(roomId, before, pageSize);

        return page.ToDto();
    }

    /// <summary>Every room on this server, for the channel list (§9.2).</summary>
    public async Task<IReadOnlyList<RoomSummary>> ListRooms()
    {
        RequireAccount();

        var all = await rooms.GetRoomsAsync();

        return [.. all.Select(room => room.ToSummary())];
    }

    /// <summary>The rooms this connection's account has joined.</summary>
    public async Task<IReadOnlyList<RoomSummary>> ListJoinedRooms()
    {
        var accountId = RequireAccount();

        var joined = await rooms.GetJoinedRoomsAsync(accountId);

        return [.. joined.Select(room => room.ToSummary())];
    }

    /// <summary>Joins this connection's account to a room.</summary>
    /// <remarks>
    /// Joining does not subscribe the connection — the two are separate on purpose, since
    /// membership outlives a connection and a subscription does not. A client that wants both calls
    /// <see cref="SubscribeToRoom"/> as well, which is also what it does after a reconnect.
    /// </remarks>
    public async Task JoinRoom(Guid roomId)
    {
        var accountId = RequireAccount();

        var result = await rooms.JoinAsync(roomId, accountId);

        if (!result.Succeeded)
        {
            throw Refuse(result.Status);
        }
    }

    /// <summary>Removes this connection's account from a room.</summary>
    public async Task LeaveRoom(Guid roomId)
    {
        var accountId = RequireAccount();

        var result = await rooms.LeaveAsync(roomId, accountId);

        if (!result.Succeeded)
        {
            throw Refuse(result.Status);
        }
    }

    /// <summary>The group every connection watching a room belongs to.</summary>
    /// <remarks>
    /// <para>
    /// Prefixed, so room groups cannot collide with the <c>presence:{pubkey}</c> groups §8.2 uses on
    /// this same connection. Two features naming groups in one namespace is how a subscription to
    /// one silently delivers the other.
    /// </para>
    /// <para>
    /// A room id rather than a name: names change (§5.4.1), and a rename must not strand every
    /// connection watching the room in a group nothing publishes to any more.
    /// </para>
    /// </remarks>
    private static string RoomGroup(Guid roomId) => $"room:{roomId:D}";

    /// <summary>
    /// The account this connection authenticated as.
    /// </summary>
    /// <remarks>
    /// Should never fail: <c>[Authorize]</c> refused the handshake otherwise, and
    /// <see cref="OnConnectedAsync"/> aborted anything that got past it without an account id.
    /// </remarks>
    private Guid RequireAccount()
        => connections.FindAccount(Context.ConnectionId)
            ?? Context.User?.GetAccountId()
            ?? throw new HubException("This connection is not associated with an account.");

    /// <summary>
    /// Refuses the call unless the room exists.
    /// </summary>
    /// <remarks>
    /// Checked for reads and subscriptions, which <see cref="RoomManager"/> answers harmlessly for a
    /// room that is not there — an empty page and an empty group are both indistinguishable from a
    /// room with nothing in it. Saying so is what lets a client tell "this room is empty" from
    /// "this room is gone", which is the difference between showing an empty channel and closing it.
    /// </remarks>
    private async Task RequireRoomAsync(Guid roomId)
    {
        RequireAccount();

        var exists = await rooms.RoomExistsAsync(roomId);

        if (!exists)
        {
            throw Refuse(RoomMutationStatus.NotFound);
        }
    }

    /// <summary>
    /// Turns a refusal into the fault a SignalR client sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HubException"/> specifically: its message is the one kind of server-side exception
    /// detail SignalR sends to the client, so anything else would reach the caller as an opaque
    /// "an error occurred" and leave a legitimate refusal indistinguishable from a server fault.
    /// </para>
    /// <para>
    /// These say only which rule was broken, and every one of them is something the caller already
    /// knows: which room they named, and whether they had joined it. Unlike a failed login (§4.7),
    /// there is nothing to withhold here — this connection is already authenticated, and room names
    /// are visible to every member through <see cref="ListRooms"/> anyway.
    /// </para>
    /// </remarks>
    private static HubException Refuse(RoomMutationStatus status) => status switch
    {
        RoomMutationStatus.NotFound => new HubException("No such room."),
        RoomMutationStatus.NotAMember => new HubException("You are not a member of that room."),
        RoomMutationStatus.InvalidBody => new HubException("That message is empty or too long."),
        RoomMutationStatus.InvalidName => new HubException("That room name is unusable or taken."),
        RoomMutationStatus.InvalidTopic => new HubException("That topic is too long."),
        _ => new HubException("That is not allowed."),
    };
}

