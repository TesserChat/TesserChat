using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Auditing;
using TesserChat.Server.Persistence;

namespace TesserChat.Server.Authorization;

/// <summary>
/// Creates roles, edits what they grant, and assigns them to members (§5.3).
/// </summary>
/// <remarks>
/// <para>
/// This is where §5.3's invariants are enforced — in the mutation layer, not in the UI. A client is
/// free to be wrong about what it offers; the rules hold here regardless of what any client shows,
/// and regardless of who is asking:
/// </para>
/// <list type="bullet">
/// <item>A server always has at least one account holding the Owner role.</item>
/// <item>A system role cannot be deleted.</item>
/// <item>The Owner role's permission set cannot be edited, because it is implicit.</item>
/// </list>
/// <para>
/// <b>This class does not check whether the caller is allowed to act.</b> That is
/// <see cref="PermissionResolver"/>'s job at the endpoint, and mixing the two would make the
/// invariants above depend on the caller's permissions — which is exactly what they must not do.
/// The last-Owner rule binds an Owner too.
/// </para>
/// </remarks>
internal sealed class RoleManager(
    TesserChatDbContext context,
    PermissionResolver resolver,
    AuditLog auditLog,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Creates a role holding no permissions.
    /// </summary>
    /// <remarks>
    /// Empty rather than pre-populated: a new role should grant nothing until someone says what it
    /// grants. Permissions are added with <see cref="SetPermissionsAsync"/>.
    /// </remarks>
    public async Task<(RoleMutationResult Result, Role? Role)> CreateRoleAsync(
        string name,
        Guid? actorAccountId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormaliseName(name, out var normalised))
        {
            return (RoleMutationResult.Refused(RoleMutationStatus.InvalidName), null);
        }

        if (await context.Roles.AnyAsync(role => role.Name == normalised, cancellationToken))
        {
            return (RoleMutationResult.Refused(RoleMutationStatus.InvalidName), null);
        }

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = normalised,
            IsSystemRole = false,
            IsOwner = false,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        context.Roles.Add(role);

        // Recorded before saving, so the entry shares the transaction with the change it describes
        // (§5.5) — a committed change always has its entry, a rolled-back one leaves none.
        auditLog.Record(
            AuditAction.RoleCreated,
            $"Created role '{normalised}'.",
            actorAccountId,
            targetRoleId: role.Id);

        await context.SaveChangesAsync(cancellationToken);

        return (RoleMutationResult.Applied(), role);
    }

    /// <summary>
    /// Renames a role. Permitted on system roles, including the Owner.
    /// </summary>
    /// <remarks>
    /// Renaming the Owner is safe precisely because nothing resolves authority by name — the
    /// <see cref="Role.IsOwner"/> flag carries it. A server that calls its Owner "Founder" still
    /// has an Owner.
    /// </remarks>
    public async Task<RoleMutationResult> RenameRoleAsync(
        Guid roleId,
        string name,
        Guid? actorAccountId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormaliseName(name, out var normalised))
        {
            return RoleMutationResult.Refused(RoleMutationStatus.InvalidName);
        }

        var role = await context.Roles.FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return RoleMutationResult.Refused(RoleMutationStatus.NotFound);
        }

        if (string.Equals(role.Name, normalised, StringComparison.Ordinal))
        {
            return RoleMutationResult.NoChange();
        }

        if (await context.Roles.AnyAsync(r => r.Name == normalised && r.Id != roleId, cancellationToken))
        {
            return RoleMutationResult.Refused(RoleMutationStatus.InvalidName);
        }

        var previousName = role.Name;
        role.Name = normalised;

        auditLog.Record(
            AuditAction.RoleRenamed,
            $"Renamed role '{previousName}' to '{normalised}'.",
            actorAccountId,
            targetRoleId: roleId);

        await context.SaveChangesAsync(cancellationToken);

        return RoleMutationResult.Applied();
    }

    /// <summary>
    /// Deletes a role and every assignment of it. System roles are refused.
    /// </summary>
    public async Task<RoleMutationResult> DeleteRoleAsync(
        Guid roleId,
        Guid? actorAccountId = null,
        CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return RoleMutationResult.Refused(RoleMutationStatus.NotFound);
        }

        // Covers the Owner too, which is a system role — deleting it would remove the server's
        // only source of unrestricted authority.
        if (role.IsSystemRole)
        {
            return RoleMutationResult.Refused(RoleMutationStatus.SystemRoleImmutable);
        }

        context.Roles.Remove(role);

        // The role id is recorded too, but the name is what keeps this readable once the role it
        // names no longer exists.
        auditLog.Record(
            AuditAction.RoleDeleted,
            $"Deleted role '{role.Name}'.",
            actorAccountId,
            targetRoleId: roleId);

        await context.SaveChangesAsync(cancellationToken);

        return RoleMutationResult.Applied();
    }

    /// <summary>
    /// Replaces the set of permissions a role grants.
    /// </summary>
    /// <remarks>
    /// A replace rather than add/remove calls, because a role editor sends the state it wants and
    /// computing the difference here avoids a client having to send two lists in the right order.
    /// Unknown permission keys are refused outright rather than ignored, so a typo does not quietly
    /// produce a role granting less than the caller believes.
    /// </remarks>
    public async Task<RoleMutationResult> SetPermissionsAsync(
        Guid roleId,
        IReadOnlyCollection<string> permissionKeys,
        Guid? actorAccountId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissionKeys);

        var role = await context.Roles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);

        if (role is null)
        {
            return RoleMutationResult.Refused(RoleMutationStatus.NotFound);
        }

        // The Owner holds everything implicitly (see PermissionResolver). Accepting an edit here
        // would either do nothing or imply its authority can be reduced; refusing says so plainly.
        if (role.IsOwner)
        {
            return RoleMutationResult.Refused(RoleMutationStatus.SystemRoleImmutable);
        }

        var requested = permissionKeys.ToHashSet(StringComparer.Ordinal);

        var known = Permission.All.Select(permission => permission.Key).ToHashSet(StringComparer.Ordinal);
        if (!requested.IsSubsetOf(known))
        {
            return RoleMutationResult.Refused(RoleMutationStatus.NotFound);
        }

        var current = role.Permissions.Select(grant => grant.PermissionKey).ToHashSet(StringComparer.Ordinal);
        if (current.SetEquals(requested))
        {
            return RoleMutationResult.NoChange();
        }

        foreach (var removed in role.Permissions.Where(grant => !requested.Contains(grant.PermissionKey)).ToList())
        {
            role.Permissions.Remove(removed);
            context.RolePermissions.Remove(removed);
        }

        foreach (var added in requested.Except(current))
        {
            context.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionKey = added });
        }

        // What changed, not what the set now is: "removed messages.delete" is the auditable fact,
        // and the resulting set is derivable from the role itself.
        var granted = requested.Except(current).Order().ToList();
        var withdrawn = current.Except(requested).Order().ToList();

        var summary = string.Join(
            ' ',
            new[]
            {
                granted.Count > 0 ? $"Granted {string.Join(", ", granted)}." : null,
                withdrawn.Count > 0 ? $"Withdrew {string.Join(", ", withdrawn)}." : null,
            }.Where(part => part is not null));

        auditLog.Record(
            AuditAction.RolePermissionsChanged,
            $"Role '{role.Name}': {summary}",
            actorAccountId,
            targetRoleId: roleId);

        await context.SaveChangesAsync(cancellationToken);

        return RoleMutationResult.Applied();
    }

    /// <summary>
    /// Grants a role to an account. Granting a role it already holds succeeds without writing.
    /// </summary>
    public async Task<RoleMutationResult> AssignRoleAsync(
        Guid accountId,
        Guid roleId,
        Guid? actorAccountId = null,
        CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(account => account.Id == accountId, cancellationToken);
        var roleExists = await context.Roles.AnyAsync(role => role.Id == roleId, cancellationToken);

        if (!accountExists || !roleExists)
        {
            return RoleMutationResult.Refused(RoleMutationStatus.NotFound);
        }

        var alreadyHeld = await context.AccountRoles.AnyAsync(
            assignment => assignment.AccountId == accountId && assignment.RoleId == roleId,
            cancellationToken);

        if (alreadyHeld)
        {
            return RoleMutationResult.NoChange();
        }

        context.AccountRoles.Add(new AccountRole
        {
            AccountId = accountId,
            RoleId = roleId,
            GrantedAt = timeProvider.GetUtcNow(),
        });

        var roleName = await context.Roles
            .Where(role => role.Id == roleId)
            .Select(role => role.Name)
            .SingleAsync(cancellationToken);

        auditLog.Record(
            AuditAction.RoleGranted,
            $"Granted role '{roleName}'.",
            actorAccountId,
            targetAccountId: accountId,
            targetRoleId: roleId);

        await context.SaveChangesAsync(cancellationToken);

        return RoleMutationResult.Applied();
    }

    /// <summary>
    /// Revokes a role from an account, unless doing so would leave the server without an Owner.
    /// </summary>
    /// <remarks>
    /// The last-Owner check binds everyone, an Owner acting on themselves included: a server with
    /// no Owner has nobody able to appoint one, so this is not a permission anyone can hold. A
    /// server with two Owners can freely demote either.
    /// </remarks>
    public async Task<RoleMutationResult> RevokeRoleAsync(
        Guid accountId,
        Guid roleId,
        Guid? actorAccountId = null,
        CancellationToken cancellationToken = default)
    {
        var assignment = await context.AccountRoles
            .Include(a => a.Role)
            .FirstOrDefaultAsync(
                a => a.AccountId == accountId && a.RoleId == roleId,
                cancellationToken);

        if (assignment is null)
        {
            // Nothing to revoke. Reported as a no-op rather than an error: the caller asked for a
            // state that already holds.
            return RoleMutationResult.NoChange();
        }

        if (assignment.Role!.IsOwner && await resolver.CountOwnersAsync(cancellationToken) <= 1)
        {
            return RoleMutationResult.Refused(RoleMutationStatus.WouldRemoveLastOwner);
        }

        context.AccountRoles.Remove(assignment);

        auditLog.Record(
            AuditAction.RoleRevoked,
            $"Revoked role '{assignment.Role.Name}'.",
            actorAccountId,
            targetAccountId: accountId,
            targetRoleId: roleId);

        await context.SaveChangesAsync(cancellationToken);

        return RoleMutationResult.Applied();
    }

    /// <summary>
    /// The roles an account holds, ordered by name.
    /// </summary>
    public async Task<IReadOnlyList<Role>> GetRolesForAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
        => await context.AccountRoles
            .Where(assignment => assignment.AccountId == accountId)
            .Select(assignment => assignment.Role!)
            .OrderBy(role => role.Name)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Trims a role name and checks it against the stored bound.
    /// </summary>
    private static bool TryNormaliseName(string? name, out string normalised)
    {
        normalised = name?.Trim() ?? string.Empty;
        return normalised.Length > 0 && normalised.Length <= Role.NameMaxLength;
    }
}
