namespace TesserChat.Server.Rooms;

/// <summary>
/// One page of a room's history, newest first (§5.4).
/// </summary>
/// <param name="Messages">
/// The messages, ordered newest first — the order a client scrolling back consumes them in.
/// </param>
/// <param name="NextBefore">
/// The cursor to pass as <c>before</c> to fetch the next page back, or <see langword="null"/> when
/// the page reached the start of the room's history.
/// </param>
/// <remarks>
/// <para>
/// <b>Keyset paging, not offset paging.</b> The cursor is the id of the oldest message on this page
/// and the next call asks for messages below it. An <c>OFFSET</c> would shift under a client that
/// pauses mid-scroll while new messages arrive, which is how a pager repeats a message or skips
/// one; a keyset cursor names a fixed point in the sequence and is unaffected by anything posted
/// since.
/// </para>
/// <para>
/// <see cref="NextBefore"/> being null means this page ended the history. A caller must not infer
/// that from a short page — a full page that happens to end at the first message is
/// indistinguishable otherwise.
/// </para>
/// </remarks>
internal readonly record struct MessagePage(
    IReadOnlyList<Persistence.RoomMessage> Messages,
    long? NextBefore);
