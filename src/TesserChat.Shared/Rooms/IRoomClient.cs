namespace TesserChat.Shared.Rooms;

/// <summary>
/// What the server pushes to a connected client about rooms (§6, §5.4.1).
/// </summary>
/// <remarks>
/// <para>
/// Declared in <c>TesserChat.Shared</c> and implemented as <c>Hub&lt;IRoomClient&gt;</c> on the
/// server, so the names and shapes of these calls are checked at compile time on both sides. A hub
/// that pushed by string name would let the server and client disagree silently — the failure being
/// a message that simply never arrives.
/// </para>
/// <para>
/// <b>These are notifications, not requests.</b> Nothing here returns a value: a client that missed
/// one because it was offline catches up by reading history (§5.4.1), not by having the server
/// retry. Room chat has no delivery queue — that is the DM mailbox (§7.4), and it exists there
/// because a DM has one recipient who may be offline, whereas a room's history is on the server
/// already.
/// </para>
/// </remarks>
public interface IRoomClient
{
    /// <summary>
    /// A message was posted to a room this connection is subscribed to.
    /// </summary>
    /// <remarks>
    /// Sent to every connection subscribed to the room, including the sender's own. Echoing to the
    /// sender is deliberate: it is what gives the client the server-assigned id and timestamp for
    /// the message it just sent, so an optimistically-rendered message can be reconciled against
    /// what was actually stored rather than guessed at.
    /// </remarks>
    Task MessagePosted(RoomMessageDto message);
}
