namespace TesserChat.Server.Persistence;

/// <summary>
/// One message posted in a room, stored permanently (§5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Plaintext, on purpose.</b> Rooms are not end-to-end encrypted (§7 opening). The server holds
/// the readable text because serving history to a member who was absent when it was posted requires
/// exactly that. Anyone reasoning about privacy here should read §7: DMs are the encrypted channel,
/// and nothing about this table should be made to look like a weaker version of one.
/// </para>
/// <para>
/// <b>Permanent.</b> Unlike the DM mailbox queue (§7.4), which is transient and swept, this table
/// is the room's history and nothing ages out of it. A retention policy would be a deliberate
/// feature, not a default.
/// </para>
/// </remarks>
internal sealed class RoomMessage
{
    /// <summary>
    /// Longest message body accepted.
    /// </summary>
    /// <remarks>
    /// Bounded because the body arrives from a client and is stored forever; an unbounded column is
    /// how one member fills an operator's disk. Chosen generously enough that a code block or a
    /// long paragraph is never truncated in practice (§9.4 renders Markdown).
    /// </remarks>
    public const int BodyMaxLength = 4000;

    /// <summary>
    /// Monotonic identifier, assigned by the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sequence rather than a GUID, for the same reason <see cref="AuditEntry.Id"/> is one: this
    /// is what history is ordered and paged by. Ordering by timestamp alone ties for two messages
    /// posted in the same instant and gives a pager no stable cursor — the classic way a client
    /// scrolling back either repeats a message or steps over one.
    /// </para>
    /// <para>
    /// It is server-assigned, so it also cannot be influenced by a client. A client-supplied
    /// ordering key would let one member insert themselves anywhere in a room's history.
    /// </para>
    /// </remarks>
    public long Id { get; init; }

    /// <summary>The room it was posted in.</summary>
    public Guid RoomId { get; init; }

    /// <summary>
    /// The account that wrote it (§5.1).
    /// </summary>
    /// <remarks>
    /// The account id, never the display name — §5.1 makes the UUID the identifier used for
    /// authorship, so a member renaming themselves renames them everywhere at once rather than
    /// leaving old messages attributed to a name they no longer use.
    /// </remarks>
    public Guid AuthorAccountId { get; init; }

    /// <summary>When it was posted, as recorded by the server.</summary>
    /// <remarks>
    /// The server's clock, not the client's. A client-supplied timestamp is a value a client can
    /// choose, and this one is shown to every member in the room.
    /// </remarks>
    public DateTimeOffset PostedAt { get; init; }

    /// <summary>The message text, as the author typed it.</summary>
    /// <remarks>
    /// Stored raw, not pre-rendered. Markdown is rendered by the client (§9.4), so the server never
    /// has to agree with the client about a rendering, and a rendering change does not have to
    /// rewrite history.
    /// </remarks>
    public string Body { get; set; } = string.Empty;

    /// <summary>Navigation to the room.</summary>
    public Room? Room { get; init; }

    /// <summary>Navigation to the author's account.</summary>
    public Account? Author { get; init; }
}
