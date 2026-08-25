namespace TesserChat.Server.Persistence;

/// <summary>
/// One room on this server: a named, persistent, plaintext channel (§5.4, §7 opening).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rooms are deliberately not end-to-end encrypted.</b> A room is open community chat and its
/// messages reach the server as plaintext, which is what lets the server persist them and serve
/// history to a member who was not present when they were posted. That is the design (§7 opening),
/// and it is the whole reason rooms and DMs are different subsystems rather than one with a flag.
/// </para>
/// <para>
/// A room belongs to the server rather than to whoever made it. There is no owner column: authority
/// over a room comes from the role system (§5.3), so a room does not outlive its creator's
/// permissions or become unmanageable when they leave.
/// </para>
/// </remarks>
internal sealed class Room
{
    /// <summary>
    /// Longest room name allowed.
    /// </summary>
    /// <remarks>
    /// A bound rather than a considered maximum, as with <see cref="Account.DisplayNameMaxLength"/>:
    /// names are rendered in a fixed-width channel list (§9.2). Widening later is a cheap migration.
    /// </remarks>
    public const int NameMaxLength = 64;

    /// <summary>Longest topic allowed.</summary>
    public const int TopicMaxLength = 512;

    /// <summary>The room's permanent identifier.</summary>
    /// <remarks>
    /// Generated, unlike <see cref="Account.Id"/>: an account id is derived from a public key
    /// because the same key must always resolve to the same account, whereas a room is created by
    /// this server and has nothing to derive from. Renaming a room therefore does not change what
    /// its messages point at.
    /// </remarks>
    public Guid Id { get; init; }

    /// <summary>
    /// The room's name, unique on this server.
    /// </summary>
    /// <remarks>
    /// Unique because a member reading a room's name in the channel list and in a mention has to be
    /// reading about one room. Cosmetic in the sense that it may be changed freely — nothing keys
    /// off it, <see cref="Id"/> does — but not free-form in the sense of allowing duplicates.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the room is for. Empty is normal, not missing.</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>When the room was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The account that created it, when it is known.
    /// </summary>
    /// <remarks>
    /// Nullable and deliberately not a foreign key, for the same reason
    /// <see cref="AuditEntry.ActorAccountId"/> is not: a room must survive the deletion of the
    /// account that made it. Null also covers a room created by the server itself rather than by a
    /// member.
    /// </remarks>
    public Guid? CreatedByAccountId { get; init; }

    /// <summary>Who has joined (§5.4).</summary>
    public ICollection<RoomMembership> Members { get; } = [];

    /// <summary>The room's messages, oldest first when loaded.</summary>
    public ICollection<RoomMessage> Messages { get; } = [];
}
