namespace TesserChat.Server.Persistence;

/// <summary>
/// A permission key this server enforces, as stored (§5.3).
/// </summary>
/// <remarks>
/// Rows are seeded from <see cref="Authorization.Permission.All"/> and are not created at runtime:
/// a key means something only where the server checks it, so the catalogue lives in code and the
/// table mirrors it. The row exists so <c>role_permissions</c> has something to reference and so
/// the client can render a role editor without shipping its own copy of the list.
/// </remarks>
internal sealed class PermissionGrant
{
    /// <summary>
    /// The dotted <c>area.action</c> key. This is the primary key — the string is the identity, and
    /// a surrogate id would add a join without adding meaning.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable description, shown in the role editor.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The roles granting this permission.</summary>
    public ICollection<RolePermission> Roles { get; } = [];
}

/// <summary>
/// Join row: one role grants one permission (§5.3).
/// </summary>
/// <remarks>
/// An explicit entity rather than an implicit many-to-many, so the pair can carry its own columns
/// later — when a permission was granted, and by whom, are the obvious candidates once the audit
/// log lands (§5.5).
/// </remarks>
internal sealed class RolePermission
{
    /// <summary>The role being granted a permission.</summary>
    public Guid RoleId { get; init; }

    /// <summary>The permission key granted.</summary>
    public string PermissionKey { get; init; } = string.Empty;

    /// <summary>Navigation to the role.</summary>
    public Role? Role { get; init; }

    /// <summary>Navigation to the permission.</summary>
    public PermissionGrant? Permission { get; init; }
}

/// <summary>
/// Join row: one account holds one role (§5.3).
/// </summary>
/// <remarks>
/// <para>
/// The <c>user_roles</c> table of the §5.3 sketch, named for the <see cref="Account"/> it points at
/// rather than for "user", since account is the term the rest of the server uses.
/// </para>
/// <para>
/// An account may hold any number of roles; its effective permissions are the union of what they
/// grant. Holding no roles is valid and resolves to no permissions.
/// </para>
/// </remarks>
internal sealed class AccountRole
{
    /// <summary>The account holding the role.</summary>
    public Guid AccountId { get; init; }

    /// <summary>The role held.</summary>
    public Guid RoleId { get; init; }

    /// <summary>When the role was granted.</summary>
    public DateTimeOffset GrantedAt { get; init; }

    /// <summary>Navigation to the account.</summary>
    public Account? Account { get; init; }

    /// <summary>Navigation to the role.</summary>
    public Role? Role { get; init; }
}
