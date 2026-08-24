using TesserChat.Server.Authorization;
using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests.Authorization;

/// <summary>
/// Covers permission resolution (§5.3).
/// </summary>
/// <remarks>
/// Per §0.1 these test resolution <i>generally</i> — custom roles with arbitrary permission sets —
/// not the behaviour of the three seeded roles. The system is dynamic, so tests that only exercised
/// Owner/Admin/Member would pass on an implementation that hardcoded exactly those.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class PermissionResolverTests(PostgresFixture postgres)
{
    [RequiresDockerFact]
    public async Task AnAccountWithNoRoles_HasNoPermissions()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Nobody");

        Assert.Empty(await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account)));

        foreach (var permission in Permission.All)
        {
            Assert.False(await server.ResolveAsync(resolver =>
                resolver.HasPermissionAsync(account, permission)));
        }
    }

    [RequiresDockerFact]
    public async Task AnUnknownAccount_ResolvesToNoPermissions()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);

        // Absence is denial, not an error — a caller gating an action has nothing to do with the
        // difference between "no such account" and "not allowed".
        Assert.False(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(Guid.NewGuid(), Permission.MembersKick)));

        Assert.Empty(await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(Guid.NewGuid())));
    }

    [RequiresDockerFact]
    public async Task ACustomRole_GrantsExactlyThePermissionsItHolds()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Moderator");

        var (created, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Message Sweeper"));
        Assert.True(created.Succeeded);

        await server.ManageAsync(manager => manager.SetPermissionsAsync(
            role!.Id,
            [Permission.MessagesDelete.Key]));

        await server.ManageAsync(manager => manager.AssignRoleAsync(account, role!.Id));

        // Granted.
        Assert.True(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.MessagesDelete)));

        // Everything else is not, including permissions of the seeded roles this account lacks.
        Assert.False(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.MembersKick)));
        Assert.False(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.ServerManage)));

        var resolved = await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account));
        Assert.Equal([Permission.MessagesDelete.Key], resolved);
    }

    [RequiresDockerFact]
    public async Task PermissionsFromSeveralRoles_AreUnioned()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Multi");

        var (_, kicker) = await server.ManageAsync(manager => manager.CreateRoleAsync("Kicker"));
        var (_, deleter) = await server.ManageAsync(manager => manager.CreateRoleAsync("Deleter"));

        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(kicker!.Id, [Permission.MembersKick.Key]));
        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(deleter!.Id, [Permission.MessagesDelete.Key]));

        await server.ManageAsync(manager => manager.AssignRoleAsync(account, kicker!.Id));
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, deleter!.Id));

        var resolved = await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account));

        Assert.Equal(
            new[] { Permission.MembersKick.Key, Permission.MessagesDelete.Key }.Order(),
            resolved.Order());
    }

    [RequiresDockerFact]
    public async Task APermissionGrantedByTwoRoles_ResolvesOnce()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Overlapping");

        var (_, first) = await server.ManageAsync(manager => manager.CreateRoleAsync("First"));
        var (_, second) = await server.ManageAsync(manager => manager.CreateRoleAsync("Second"));

        // Both roles grant the same permission.
        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(first!.Id, [Permission.MembersKick.Key]));
        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(second!.Id, [Permission.MembersKick.Key]));

        await server.ManageAsync(manager => manager.AssignRoleAsync(account, first!.Id));
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, second!.Id));

        var resolved = await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account));

        // A set, not a tally: the caller should not have to care how many roles contributed it.
        Assert.Single(resolved);
        Assert.Equal([Permission.MembersKick.Key], resolved);
    }

    [RequiresDockerFact]
    public async Task RevokingOneOfTwoRolesGrantingAPermission_LeavesItHeld()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Overlapping");

        var (_, first) = await server.ManageAsync(manager => manager.CreateRoleAsync("First"));
        var (_, second) = await server.ManageAsync(manager => manager.CreateRoleAsync("Second"));

        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(first!.Id, [Permission.MembersKick.Key]));
        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(second!.Id, [Permission.MembersKick.Key]));

        await server.ManageAsync(manager => manager.AssignRoleAsync(account, first!.Id));
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, second!.Id));

        await server.ManageAsync(manager => manager.RevokeRoleAsync(account, first!.Id));

        // Still granted by the role that remains.
        Assert.True(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.MembersKick)));

        await server.ManageAsync(manager => manager.RevokeRoleAsync(account, second!.Id));

        Assert.False(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.MembersKick)));
    }

    [RequiresDockerFact]
    public async Task TheOwnerRole_HoldsEveryPermissionImplicitly()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Founder");

        var owner = await server.GetRoleAsync(SystemRoles.OwnerName);
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, owner.Id));

        // Every permission, including any added to the catalogue after this test was written.
        foreach (var permission in Permission.All)
        {
            Assert.True(
                await server.ResolveAsync(resolver => resolver.HasPermissionAsync(account, permission)),
                $"The Owner should hold {permission.Key}.");
        }

        var resolved = await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account));
        Assert.Equal(Permission.All.Select(permission => permission.Key).Order(), resolved.Order());
    }

    [RequiresDockerFact]
    public async Task TheOwnersAuthority_DoesNotComeFromItsGrantedRows()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Founder");

        var owner = await server.GetRoleAsync(SystemRoles.OwnerName);
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, owner.Id));

        // The Owner is seeded with no role_permissions rows at all — its authority is the flag, not
        // a set of grants an administrator could uncheck.
        var grantedRows = await server.QueryAsync(async context =>
            context.RolePermissions.Count(grant => grant.RoleId == owner.Id));

        Assert.Equal(0, grantedRows);
        Assert.True(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.ServerManage)));
    }

    [RequiresDockerFact]
    public async Task RenamingTheOwnerRole_DoesNotCostItItsAuthority()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Founder");

        var owner = await server.GetRoleAsync(SystemRoles.OwnerName);
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, owner.Id));

        var renamed = await server.ManageAsync(manager => manager.RenameRoleAsync(owner.Id, "Founder"));
        Assert.True(renamed.Succeeded);

        // Nothing resolves authority by name, so a renamed Owner is still the Owner.
        Assert.True(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(account)));
        Assert.True(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.ServerManage)));
    }

    [RequiresDockerFact]
    public async Task TheSeededAdminRole_HoldsItsSeededPermissions_AndNotServerManage()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Admin");

        var admin = await server.GetRoleAsync(SystemRoles.AdminName);
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, admin.Id));

        foreach (var permission in SystemRoles.AdminPermissions)
        {
            Assert.True(
                await server.ResolveAsync(resolver => resolver.HasPermissionAsync(account, permission)),
                $"Admin should hold {permission.Key}.");
        }

        // Server-level settings stay with the Owner until an operator says otherwise.
        Assert.False(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.ServerManage)));
        Assert.False(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(account)));
    }

    [RequiresDockerFact]
    public async Task TheSeededMemberRole_GrantsNoAdministrativePermission()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Member");

        var member = await server.GetRoleAsync(SystemRoles.MemberName);
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, member.Id));

        Assert.Empty(await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account)));
    }

    [RequiresDockerFact]
    public async Task EditingACustomRole_ChangesWhatItsHoldersResolve()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Holder");

        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Evolving"));
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, role!.Id));

        // A new role grants nothing until someone says what it grants.
        Assert.Empty(await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account)));

        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(role!.Id, [Permission.MembersKick.Key, Permission.MembersBan.Key]));

        Assert.Equal(2, (await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account))).Count);

        // Replacing the set removes what is no longer in it.
        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(role!.Id, [Permission.MembersBan.Key]));

        var resolved = await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account));
        Assert.Equal([Permission.MembersBan.Key], resolved);
    }

    [RequiresDockerFact]
    public async Task DeletingARole_WithdrawsWhatItGranted()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Holder");

        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Temporary"));
        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(role!.Id, [Permission.MembersKick.Key]));
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, role!.Id));

        Assert.True(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.MembersKick)));

        var deleted = await server.ManageAsync(manager => manager.DeleteRoleAsync(role!.Id));
        Assert.True(deleted.Succeeded);

        // The assignment cascades away with the role rather than stranding a row.
        Assert.False(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(account, Permission.MembersKick)));
        Assert.Empty(await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account)));
    }

    [RequiresDockerFact]
    public async Task PermissionsResolve_PerAccount()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var privileged = await server.RegisterAccountAsync("Privileged");
        var ordinary = await server.RegisterAccountAsync("Ordinary");

        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Sweeper"));
        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(role!.Id, [Permission.MessagesDelete.Key]));
        await server.ManageAsync(manager => manager.AssignRoleAsync(privileged, role!.Id));

        Assert.True(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(privileged, Permission.MessagesDelete)));
        Assert.False(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(ordinary, Permission.MessagesDelete)));
    }
}
