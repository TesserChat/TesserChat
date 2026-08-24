namespace TesserChat.Server.Authorization;

/// <summary>
/// The permission keys this server enforces (§5.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Roles are data; permission keys are code.</b> An administrator can create roles freely and
/// assign them to members at will — that is the dynamic half of §5.3 and needs no migration. The
/// set of <i>keys</i> is fixed here because a key means something only where the server checks it:
/// a permission nobody enforces would appear in the role editor and grant nothing, which reads as a
/// bug rather than as flexibility.
/// </para>
/// <para>
/// Adding a permission is therefore a code change plus a seed entry, made in the same PR as the
/// enforcement point that gives it meaning.
/// </para>
/// <para>
/// Keys are dotted <c>area.action</c> strings, stable forever once shipped: they are stored in
/// <c>role_permissions</c> and renaming one would silently strip the permission from every role
/// holding it.
/// </para>
/// </remarks>
internal sealed record Permission(string Key, string Description)
{
    /// <summary>Longest permission key the column accepts.</summary>
    public const int KeyMaxLength = 64;

    /// <summary>Longest description the column accepts.</summary>
    public const int DescriptionMaxLength = 256;

    // --- Members -----------------------------------------------------------------------------

    /// <summary>Remove a member from this server.</summary>
    public static readonly Permission MembersKick = new(
        "members.kick",
        "Remove a member from this server.");

    /// <summary>Ban a member, preventing them from registering again.</summary>
    public static readonly Permission MembersBan = new(
        "members.ban",
        "Ban a member and prevent that key from registering again.");

    // --- Roles -------------------------------------------------------------------------------

    /// <summary>Create, rename, and delete roles, and choose the permissions they hold.</summary>
    public static readonly Permission RolesManage = new(
        "roles.manage",
        "Create, edit, and delete roles and the permissions they grant.");

    /// <summary>Grant and revoke roles on members.</summary>
    public static readonly Permission RolesAssign = new(
        "roles.assign",
        "Assign roles to members and remove them.");

    // --- Messages ----------------------------------------------------------------------------

    /// <summary>Delete another member's message.</summary>
    public static readonly Permission MessagesDelete = new(
        "messages.delete",
        "Delete messages posted by other members.");

    // --- Server ------------------------------------------------------------------------------

    /// <summary>Change server settings, including the connection mode (§5.2).</summary>
    public static readonly Permission ServerManage = new(
        "server.manage",
        "Change server settings, including how new members may join.");

    /// <summary>Read the moderation and administration audit log (§5.5).</summary>
    public static readonly Permission AuditLogRead = new(
        "auditlog.read",
        "Read the record of moderation and administration actions.");

    /// <summary>
    /// Every permission this server enforces, in the order the role editor should present them.
    /// </summary>
    /// <remarks>
    /// The seed reads this, so a permission added above reaches the database without a second list
    /// to keep in step.
    /// </remarks>
    public static IReadOnlyList<Permission> All { get; } =
    [
        MembersKick,
        MembersBan,
        RolesManage,
        RolesAssign,
        MessagesDelete,
        ServerManage,
        AuditLogRead,
    ];

    /// <inheritdoc />
    public override string ToString() => Key;
}
