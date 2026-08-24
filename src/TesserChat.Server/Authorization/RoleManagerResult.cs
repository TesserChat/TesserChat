namespace TesserChat.Server.Authorization;

/// <summary>
/// Why a role-management operation was refused, or that it succeeded.
/// </summary>
/// <remarks>
/// A status rather than an exception: every one of these is an ordinary outcome of input arriving
/// from a client, and the caller turns it into a response rather than handling a fault.
/// </remarks>
internal enum RoleMutationStatus
{
    /// <summary>The operation applied.</summary>
    Succeeded,

    /// <summary>The role, account, or permission named does not exist.</summary>
    NotFound,

    /// <summary>The role name was blank, too long, or already taken.</summary>
    InvalidName,

    /// <summary>
    /// The operation would have left the server without an Owner (§5.3).
    /// </summary>
    /// <remarks>
    /// Refused regardless of who asked, including an Owner acting on themselves. A server with no
    /// Owner has no one who can appoint one, so this is not a permission that anybody holds.
    /// </remarks>
    WouldRemoveLastOwner,

    /// <summary>
    /// The target is a system role and the operation is not allowed on one (§5.3).
    /// </summary>
    /// <remarks>
    /// Deleting a seeded role is refused: a server with no Member role has nothing to give a new
    /// member. Editing what a system role grants is allowed — an operator may legitimately want a
    /// more or less powerful Admin — with the Owner as the exception, since its authority is
    /// implicit rather than granted.
    /// </remarks>
    SystemRoleImmutable,
}

/// <summary>
/// The outcome of a role-management operation.
/// </summary>
/// <param name="Status">Whether it applied, and why not if it did not.</param>
/// <param name="Changed">
/// Whether anything was actually written. A no-op that was already true — granting a role the
/// account already holds — succeeds with <see langword="false"/>, so a caller writing an audit
/// entry (§5.5) does not record a change that did not happen.
/// </param>
internal readonly record struct RoleMutationResult(RoleMutationStatus Status, bool Changed)
{
    /// <summary>Whether the operation applied.</summary>
    public bool Succeeded => Status == RoleMutationStatus.Succeeded;

    internal static RoleMutationResult Applied() => new(RoleMutationStatus.Succeeded, Changed: true);

    internal static RoleMutationResult NoChange() => new(RoleMutationStatus.Succeeded, Changed: false);

    internal static RoleMutationResult Refused(RoleMutationStatus status) => new(status, Changed: false);
}
