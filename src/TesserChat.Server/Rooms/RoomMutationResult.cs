namespace TesserChat.Server.Rooms;

/// <summary>
/// Why a room operation was refused, or that it succeeded.
/// </summary>
/// <remarks>
/// A status rather than an exception, matching <c>RoleMutationStatus</c>: every one of these is an
/// ordinary outcome of input arriving from a client, and the caller turns it into a response rather
/// than handling a fault.
/// </remarks>
internal enum RoomMutationStatus
{
    /// <summary>The operation applied.</summary>
    Succeeded,

    /// <summary>The room or account named does not exist.</summary>
    NotFound,

    /// <summary>The room name was blank, too long, or already taken.</summary>
    InvalidName,

    /// <summary>The topic was longer than the column accepts.</summary>
    InvalidTopic,

    /// <summary>
    /// The message body was blank or longer than the column accepts.
    /// </summary>
    /// <remarks>
    /// Blank is refused rather than stored: an empty message says nothing and would occupy a row in
    /// permanent history forever (§5.4).
    /// </remarks>
    InvalidBody,

    /// <summary>
    /// The account is not a member of the room it tried to post in (§5.4).
    /// </summary>
    /// <remarks>
    /// Posting requires membership; reading history does not. See <c>RoomMembership</c>.
    /// </remarks>
    NotAMember,
}

/// <summary>
/// The outcome of a room operation.
/// </summary>
/// <param name="Status">Whether it applied, and why not if it did not.</param>
/// <param name="Changed">
/// Whether anything was actually written. Joining a room the account is already in succeeds with
/// <see langword="false"/>, so a caller does not announce a join that did not happen.
/// </param>
internal readonly record struct RoomMutationResult(RoomMutationStatus Status, bool Changed)
{
    /// <summary>Whether the operation applied.</summary>
    public bool Succeeded => Status == RoomMutationStatus.Succeeded;

    internal static RoomMutationResult Applied() => new(RoomMutationStatus.Succeeded, Changed: true);

    internal static RoomMutationResult NoChange() => new(RoomMutationStatus.Succeeded, Changed: false);

    internal static RoomMutationResult Refused(RoomMutationStatus status) => new(status, Changed: false);
}
