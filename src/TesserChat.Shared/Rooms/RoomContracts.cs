namespace TesserChat.Shared.Rooms;

/// <summary>
/// A room, as a client sees it (§5.4.1).
/// </summary>
/// <param name="Id">The room's permanent id. What every other call names a room by.</param>
/// <param name="Name">The room's name, unique on this server and freely changeable.</param>
/// <param name="Topic">What the room is for. Empty is normal, not missing.</param>
public sealed record RoomSummary(Guid Id, string Name, string Topic);

/// <summary>
/// One message in a room (§5.4.1).
/// </summary>
/// <param name="Id">
/// The message's sequence number. History is ordered and paged by this rather than by
/// <paramref name="PostedAt"/>: two messages posted in the same instant tie on a timestamp, and a
/// pager needs a stable cursor. It is also the value passed back as <c>before</c> to page further.
/// </param>
/// <param name="RoomId">The room it was posted in.</param>
/// <param name="AuthorAccountId">
/// Who wrote it, as an account id rather than a display name (§5.1). A client resolves the name
/// itself, so a member renaming themselves renames them across all of history at once.
/// </param>
/// <param name="PostedAt">
/// When the server recorded it, ISO-8601. The server's clock, never the sender's — this is shown to
/// every member of the room, so it is not a value the sender may choose.
/// </param>
/// <param name="Body">
/// The message text exactly as typed, apart from trimmed surrounding whitespace. Markdown is
/// rendered by the client (§9.4); the server neither renders nor rewrites it.
/// </param>
public sealed record RoomMessageDto(
    long Id,
    Guid RoomId,
    Guid AuthorAccountId,
    string PostedAt,
    string Body);

/// <summary>
/// A page of a room's history, newest first (§5.4.1).
/// </summary>
/// <param name="Messages">The messages, newest first — the order a client scrolling back wants.</param>
/// <param name="NextBefore">
/// The cursor for the next page back, or <see langword="null"/> when this page reached the start of
/// the room's history.
/// </param>
/// <remarks>
/// A caller must not infer "no more history" from a short page: a full page that happens to end the
/// history is indistinguishable from one with more behind it, which is exactly why the cursor is
/// carried explicitly.
/// </remarks>
public sealed record MessagePageDto(IReadOnlyList<RoomMessageDto> Messages, long? NextBefore);
