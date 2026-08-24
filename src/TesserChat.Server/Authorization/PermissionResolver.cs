using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Persistence;

namespace TesserChat.Server.Authorization;

/// <summary>
/// Answers what an account is permitted to do on this server (§5.3).
/// </summary>
/// <remarks>
/// <para>
/// Resolution is the union of the permissions granted by every role the account holds, plus one
/// rule that is not data: <b>the Owner role holds every permission implicitly</b>. That rule lives
/// here rather than in seeded rows so an administrator cannot neuter the Owner by editing its
/// permission set — §5.3 requires a server to always have a working Owner, and a checkbox must not
/// be able to take that away.
/// </para>
/// <para>
/// Nothing here branches on a role's <i>name</i>. A server whose Owner role has been renamed still
/// has an Owner, because the flag is what carries the meaning.
/// </para>
/// </remarks>
internal sealed class PermissionResolver(TesserChatDbContext context)
{
    /// <summary>
    /// Whether <paramref name="accountId"/> holds <paramref name="permission"/>.
    /// </summary>
    /// <remarks>
    /// An account that does not exist, or holds no roles, resolves to <see langword="false"/> —
    /// absence is denial, never an error. Callers gate an action on this; there is nothing to
    /// distinguish for them between "no such account" and "not allowed".
    /// </remarks>
    public async Task<bool> HasPermissionAsync(
        Guid accountId,
        Permission permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permission);

        if (await IsOwnerAsync(accountId, cancellationToken))
        {
            return true;
        }

        return await context.AccountRoles
            .Where(assignment => assignment.AccountId == accountId)
            .SelectMany(assignment => assignment.Role!.Permissions)
            .AnyAsync(grant => grant.PermissionKey == permission.Key, cancellationToken);
    }

    /// <summary>
    /// Every permission key <paramref name="accountId"/> holds, deduplicated.
    /// </summary>
    /// <remarks>
    /// A permission granted by two of an account's roles appears once — this is a set, and the
    /// caller should not have to care how many roles happened to contribute it. Returns the full
    /// catalogue for an Owner.
    /// </remarks>
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (await IsOwnerAsync(accountId, cancellationToken))
        {
            return Permission.All.Select(permission => permission.Key).ToHashSet(StringComparer.Ordinal);
        }

        var keys = await context.AccountRoles
            .Where(assignment => assignment.AccountId == accountId)
            .SelectMany(assignment => assignment.Role!.Permissions)
            .Select(grant => grant.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);

        return keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="accountId"/> holds the Owner role.
    /// </summary>
    public async Task<bool> IsOwnerAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await context.AccountRoles
            .AnyAsync(
                assignment => assignment.AccountId == accountId && assignment.Role!.IsOwner,
                cancellationToken);

    /// <summary>
    /// How many accounts currently hold the Owner role.
    /// </summary>
    /// <remarks>
    /// The guard behind §5.3's "a server needs at least one Owner at all times" — role mutation
    /// consults this before removing an Owner assignment.
    /// </remarks>
    public async Task<int> CountOwnersAsync(CancellationToken cancellationToken = default)
        => await context.AccountRoles.CountAsync(assignment => assignment.Role!.IsOwner, cancellationToken);
}
