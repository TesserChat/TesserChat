namespace TesserChat.Server.Persistence;

/// <summary>
/// One account's membership of one room (§5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership decides who may post and who a room appears to, not what may be read.</b> §5.4
/// requires that a member can scroll history from before they joined, so joining a room cannot be
/// the thing that unlocks its past — history is readable to this server's members generally, and
/// this row records participation rather than granting access retroactively.
/// </para>
/// <para>
/// That distinction is why leaving a room is cheap and non-destructive: the row goes away, the
/// member's messages stay, and rejoining does not have to reconstruct anything.
/// </para>
/// </remarks>
internal sealed class RoomMembership
{
    /// <summary>The room joined.</summary>
    public Guid RoomId { get; init; }

    /// <summary>The account that joined.</summary>
    public Guid AccountId { get; init; }

    /// <summary>When they joined.</summary>
    /// <remarks>
    /// Reset by leaving and rejoining, since the row is deleted and recreated. This is a record of
    /// the current membership, not a history of every join — the audit log (§5.5) is where a
    /// history would belong if one is ever wanted.
    /// </remarks>
    public DateTimeOffset JoinedAt { get; init; }

    /// <summary>Navigation to the room.</summary>
    public Room? Room { get; init; }

    /// <summary>Navigation to the account.</summary>
    public Account? Account { get; init; }
}
