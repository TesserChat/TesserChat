namespace TesserChat.Server.Persistence;

/// <summary>
/// A named set of permissions that can be granted to members (§5.3).
/// </summary>
/// <remarks>
/// <para>
/// Roles are data, not an enum. A server ships with Owner, Admin, and Member, but an administrator
/// may create as many more as they like and choose exactly which permissions each holds — none of
/// which needs a schema migration. Nothing in the server may branch on a role's name.
/// </para>
/// <para>
/// There is no <c>server_id</c>: one deployment is one server with one database, the same scoping
/// <see cref="Account"/> uses. Roles are per-server because the database is.
/// </para>
/// </remarks>
internal sealed class Role
{
    /// <summary>Longest role name the column accepts.</summary>
    public const int NameMaxLength = 64;

    /// <summary>Surrogate key. Unlike an account id there is nothing to derive one from.</summary>
    public Guid Id { get; init; }

    /// <summary>
    /// The role's name, unique on this server and shown wherever roles are listed.
    /// </summary>
    /// <remarks>
    /// Unique so that a member reading "Moderator" in two places is reading about one role. This is
    /// a label for people: authorization decisions resolve permission keys, never names.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this role was created by the server rather than by an administrator.
    /// </summary>
    /// <remarks>
    /// System roles are seeded on first migration and may not be deleted — a server with no Member
    /// role has nothing to give a new member. Their permissions remain editable, since an operator
    /// may legitimately want a more or less powerful Admin. <see cref="IsOwner"/> is the stricter
    /// flag.
    /// </remarks>
    public bool IsSystemRole { get; init; }

    /// <summary>
    /// Whether this is the Owner role, which a server must always have exactly one of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate flag rather than a name comparison, because names are editable and a server whose
    /// Owner role was renamed must not thereby lose its Owner. §5.3 requires that the Owner is
    /// non-deletable and non-demotable, and that requirement cannot rest on a string.
    /// </para>
    /// <para>
    /// The Owner role implicitly holds every permission, so its <c>role_permissions</c> rows are
    /// not what makes it powerful — see <c>PermissionResolver</c>. That is what stops an
    /// administrator from neutering the Owner by unchecking boxes.
    /// </para>
    /// </remarks>
    public bool IsOwner { get; init; }

    /// <summary>When this role was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The permissions this role grants.</summary>
    public ICollection<RolePermission> Permissions { get; } = [];

    /// <summary>The members holding this role.</summary>
    public ICollection<AccountRole> Members { get; } = [];
}
