namespace TesserChat.Server.Auditing;

/// <summary>
/// The kinds of action the audit log records (§5.5).
/// </summary>
/// <remarks>
/// <para>
/// Stored as a string rather than an integer, so a row read straight out of Postgres says
/// <c>RoleGranted</c> rather than <c>3</c>. An operator reading their own audit table with
/// <c>psql</c> should not need this file open to understand it, and renumbering an enum must never
/// be able to silently rewrite the meaning of history.
/// </para>
/// <para>
/// <b>Names are frozen once shipped</b> for the same reason: the string is what is stored, so
/// renaming a member reinterprets every existing row.
/// </para>
/// <para>
/// §5.5 names role changes, kicks/bans, and message deletions as the floor. Only role changes exist
/// to be logged today — kicks, bans, and message deletion are not built. The members below cover
/// what the server can actually do; the rest arrive with the features, in the same change as the
/// call site that records them.
/// </para>
/// </remarks>
internal enum AuditAction
{
    /// <summary>First-run setup completed and the first Owner was assigned (§5.6).</summary>
    /// <remarks>
    /// Recorded with no actor: setup runs before any account exists to attribute it to. The account
    /// that claimed ownership is the target.
    /// </remarks>
    ServerSetUp,

    /// <summary>A role was created.</summary>
    RoleCreated,

    /// <summary>A role was renamed.</summary>
    RoleRenamed,

    /// <summary>A role was deleted, withdrawing it from everyone who held it.</summary>
    RoleDeleted,

    /// <summary>The set of permissions a role grants was changed.</summary>
    RolePermissionsChanged,

    /// <summary>A role was granted to an account.</summary>
    RoleGranted,

    /// <summary>A role was revoked from an account.</summary>
    RoleRevoked,
}
