using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TesserChat.Server.Auth;

namespace TesserChat.Server.Realtime;

/// <summary>
/// The authenticated real-time connection room chat and presence both ride on (§6).
/// </summary>
/// <remarks>
/// <para>
/// <b>One transport, separable responsibilities.</b> §6 puts room chat (#17) and presence (#23) on
/// the same connection, which is a connection-count decision rather than a reason to write them
/// together. This class owns the connection lifecycle and nothing else: it authenticates a
/// connection, resolves it to an account, and records it in <see cref="ConnectionRegistry"/>. The
/// features layer on top by reading that registry, so neither has to know the other exists.
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
    ILogger<TesserChatHub> logger) : Hub
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
    public Guid WhoAmI()
    {
        var accountId = connections.FindAccount(Context.ConnectionId)
            ?? Context.User?.GetAccountId();

        return accountId
            ?? throw new HubException("This connection is not associated with an account.");
    }
}
