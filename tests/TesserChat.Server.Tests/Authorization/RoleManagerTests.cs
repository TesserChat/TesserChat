using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Authorization;
using TesserChat.Server.Persistence;
using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests.Authorization;

/// <summary>
/// Covers role creation, editing, assignment, and the §5.3 invariants.
/// </summary>
/// <remarks>
/// The invariants are enforced in the mutation layer rather than the UI, so these test them there:
/// a client is free to be wrong about what it offers, and the rules must hold regardless.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class RoleManagerTests(PostgresFixture postgres)
{
    [RequiresDockerFact]
    public async Task AFreshServer_HasTheThreeSeededRoles()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);

        var roles = await server.QueryAsync(async context =>
            await context.Roles.OrderBy(role => role.Name).ToListAsync());

        Assert.Equal(
            [SystemRoles.AdminName, SystemRoles.MemberName, SystemRoles.OwnerName],
            roles.Select(role => role.Name));

        Assert.All(roles, role => Assert.True(role.IsSystemRole));
        Assert.Single(roles, role => role.IsOwner);
    }

    [RequiresDockerFact]
    public async Task TheSeededRoleIds_AreStableAcrossDeployments()
    {
        await using var first = await AuthorizationHost.StartAsync(postgres);
        await using var second = await AuthorizationHost.StartAsync(postgres);

        var fromFirst = await first.GetRoleAsync(SystemRoles.OwnerName);
        var fromSecond = await second.GetRoleAsync(SystemRoles.OwnerName);

        // Derived from the seed name rather than generated, so every deployment agrees.
        Assert.Equal(SystemRoles.OwnerId, fromFirst.Id);
        Assert.Equal(fromFirst.Id, fromSecond.Id);
    }

    [RequiresDockerFact]
    public async Task CreateRole_MakesARoleGrantingNothing()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);

        var (result, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Moderator"));

        Assert.True(result.Succeeded);
        Assert.NotNull(role);
        Assert.Equal("Moderator", role.Name);
        Assert.False(role.IsSystemRole);
        Assert.False(role.IsOwner);

        var granted = await server.QueryAsync(async context =>
            await context.RolePermissions.CountAsync(grant => grant.RoleId == role.Id));

        Assert.Equal(0, granted);
    }

    [RequiresDockerFact]
    public async Task CreateRole_RefusesABlankOrOverlongName()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);

        foreach (var name in new[] { "", "   ", "\t", new string('a', Role.NameMaxLength + 1) })
        {
            var (result, role) = await server.ManageAsync(manager => manager.CreateRoleAsync(name));

            Assert.Equal(RoleMutationStatus.InvalidName, result.Status);
            Assert.Null(role);
        }
    }

    [RequiresDockerFact]
    public async Task CreateRole_RefusesADuplicateName()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);

        await server.ManageAsync(manager => manager.CreateRoleAsync("Moderator"));
        var (second, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("  Moderator  "));

        // Trimmed before comparison, so surrounding whitespace does not sneak a duplicate past.
        Assert.Equal(RoleMutationStatus.InvalidName, second.Status);
        Assert.Null(role);
    }

    [RequiresDockerFact]
    public async Task DeleteRole_RefusesASystemRole()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);

        foreach (var name in new[] { SystemRoles.OwnerName, SystemRoles.AdminName, SystemRoles.MemberName })
        {
            var role = await server.GetRoleAsync(name);
            var result = await server.ManageAsync(manager => manager.DeleteRoleAsync(role.Id));

            Assert.Equal(RoleMutationStatus.SystemRoleImmutable, result.Status);
        }

        var remaining = await server.QueryAsync(async context => await context.Roles.CountAsync());
        Assert.Equal(3, remaining);
    }

    [RequiresDockerFact]
    public async Task SetPermissions_RefusesToEditTheOwner()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var owner = await server.GetRoleAsync(SystemRoles.OwnerName);

        // Neither granting nor clearing: the Owner's authority is implicit, so an edit here would
        // either do nothing or imply it can be reduced.
        var cleared = await server.ManageAsync(manager => manager.SetPermissionsAsync(owner.Id, []));
        Assert.Equal(RoleMutationStatus.SystemRoleImmutable, cleared.Status);

        var granted = await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(owner.Id, [Permission.MembersKick.Key]));
        Assert.Equal(RoleMutationStatus.SystemRoleImmutable, granted.Status);
    }

    [RequiresDockerFact]
    public async Task SetPermissions_EditsANonOwnerSystemRole()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var admin = await server.GetRoleAsync(SystemRoles.AdminName);

        // An operator may legitimately want a more or less powerful Admin — only the Owner is
        // fixed.
        var result = await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(admin.Id, [Permission.MessagesDelete.Key]));

        Assert.True(result.Succeeded);

        var granted = await server.QueryAsync(async context => await context.RolePermissions
            .Where(grant => grant.RoleId == admin.Id)
            .Select(grant => grant.PermissionKey)
            .ToListAsync());

        Assert.Equal([Permission.MessagesDelete.Key], granted);
    }

    [RequiresDockerFact]
    public async Task SetPermissions_RefusesAnUnknownKey()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Custom"));

        var result = await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(role!.Id, [Permission.MembersKick.Key, "messages.pin"]));

        // Refused outright rather than partially applied, so a typo cannot quietly produce a role
        // granting less than the caller believes.
        Assert.Equal(RoleMutationStatus.NotFound, result.Status);

        var granted = await server.QueryAsync(async context =>
            await context.RolePermissions.CountAsync(grant => grant.RoleId == role!.Id));

        Assert.Equal(0, granted);
    }

    [RequiresDockerFact]
    public async Task SetPermissions_ReportsNoChange_WhenTheSetAlreadyMatches()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Custom"));

        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(role!.Id, [Permission.MembersKick.Key]));

        var repeat = await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(role!.Id, [Permission.MembersKick.Key]));

        Assert.True(repeat.Succeeded);
        Assert.False(repeat.Changed);
    }

    [RequiresDockerFact]
    public async Task AssignRole_IsIdempotent()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Member");
        var member = await server.GetRoleAsync(SystemRoles.MemberName);

        var first = await server.ManageAsync(manager => manager.AssignRoleAsync(account, member.Id));
        var second = await server.ManageAsync(manager => manager.AssignRoleAsync(account, member.Id));

        Assert.True(first.Changed);
        Assert.True(second.Succeeded);
        Assert.False(second.Changed);

        var held = await server.QueryAsync(async context =>
            await context.AccountRoles.CountAsync(assignment => assignment.AccountId == account));

        Assert.Equal(1, held);
    }

    [RequiresDockerFact]
    public async Task AssignRole_RefusesAnUnknownAccountOrRole()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Member");
        var member = await server.GetRoleAsync(SystemRoles.MemberName);

        Assert.Equal(
            RoleMutationStatus.NotFound,
            (await server.ManageAsync(manager => manager.AssignRoleAsync(Guid.NewGuid(), member.Id))).Status);

        Assert.Equal(
            RoleMutationStatus.NotFound,
            (await server.ManageAsync(manager => manager.AssignRoleAsync(account, Guid.NewGuid()))).Status);
    }

    [RequiresDockerFact]
    public async Task RevokeRole_RefusesToRemoveTheLastOwner()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var founder = await server.RegisterAccountAsync("Founder");
        var owner = await server.GetRoleAsync(SystemRoles.OwnerName);

        await server.ManageAsync(manager => manager.AssignRoleAsync(founder, owner.Id));

        var result = await server.ManageAsync(manager => manager.RevokeRoleAsync(founder, owner.Id));

        // A server with no Owner has nobody able to appoint one, so this binds everyone — the
        // Owner acting on themselves included.
        Assert.Equal(RoleMutationStatus.WouldRemoveLastOwner, result.Status);
        Assert.True(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(founder)));
        Assert.Equal(1, await server.ResolveAsync(resolver => resolver.CountOwnersAsync()));
    }

    [RequiresDockerFact]
    public async Task RevokeRole_AllowsDemotion_WhenAnotherOwnerRemains()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var first = await server.RegisterAccountAsync("First Owner");
        var second = await server.RegisterAccountAsync("Second Owner");
        var owner = await server.GetRoleAsync(SystemRoles.OwnerName);

        await server.ManageAsync(manager => manager.AssignRoleAsync(first, owner.Id));
        await server.ManageAsync(manager => manager.AssignRoleAsync(second, owner.Id));

        var result = await server.ManageAsync(manager => manager.RevokeRoleAsync(first, owner.Id));

        Assert.True(result.Succeeded);
        Assert.False(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(first)));
        Assert.True(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(second)));

        // And the one that remains cannot then be removed either.
        var last = await server.ManageAsync(manager => manager.RevokeRoleAsync(second, owner.Id));
        Assert.Equal(RoleMutationStatus.WouldRemoveLastOwner, last.Status);
    }

    [RequiresDockerFact]
    public async Task RevokeRole_RemovesAnOrdinaryRoleFreely()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Member");
        var admin = await server.GetRoleAsync(SystemRoles.AdminName);

        await server.ManageAsync(manager => manager.AssignRoleAsync(account, admin.Id));
        var result = await server.ManageAsync(manager => manager.RevokeRoleAsync(account, admin.Id));

        Assert.True(result.Succeeded);
        Assert.Empty(await server.ResolveAsync(resolver => resolver.GetPermissionsAsync(account)));
    }

    [RequiresDockerFact]
    public async Task RevokeRole_ReportsNoChange_ForARoleNotHeld()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Member");
        var admin = await server.GetRoleAsync(SystemRoles.AdminName);

        var result = await server.ManageAsync(manager => manager.RevokeRoleAsync(account, admin.Id));

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
    }

    [RequiresDockerFact]
    public async Task RenameRole_RefusesADuplicateName()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Moderator"));

        var result = await server.ManageAsync(manager =>
            manager.RenameRoleAsync(role!.Id, SystemRoles.AdminName));

        Assert.Equal(RoleMutationStatus.InvalidName, result.Status);
    }

    [RequiresDockerFact]
    public async Task RenameRole_ReportsNoChange_ForTheNameItAlreadyHas()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Moderator"));

        var result = await server.ManageAsync(manager => manager.RenameRoleAsync(role!.Id, "Moderator"));

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
    }

    [RequiresDockerFact]
    public async Task ASecondOwnerRole_CannotBeCreated()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);

        // Nothing in RoleManager can set IsOwner, so this goes at the database directly to prove
        // the partial unique index is what holds the invariant rather than the code path.
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await server.QueryAsync(async context =>
            {
                context.Roles.Add(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = "Second Owner",
                    IsSystemRole = false,
                    IsOwner = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });

                return await context.SaveChangesAsync();
            }));
    }

    [RequiresDockerFact]
    public async Task GetRolesForAccount_ReturnsWhatIsHeld_OrderedByName()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Holder");

        var admin = await server.GetRoleAsync(SystemRoles.AdminName);
        var member = await server.GetRoleAsync(SystemRoles.MemberName);

        await server.ManageAsync(manager => manager.AssignRoleAsync(account, member.Id));
        await server.ManageAsync(manager => manager.AssignRoleAsync(account, admin.Id));

        var held = await server.ManageAsync(manager => manager.GetRolesForAccountAsync(account));

        Assert.Equal([SystemRoles.AdminName, SystemRoles.MemberName], held.Select(role => role.Name));
    }

    [RequiresDockerFact]
    public async Task DeletingAnAccount_TakesItsRoleAssignmentsWithIt()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var account = await server.RegisterAccountAsync("Departing");
        var member = await server.GetRoleAsync(SystemRoles.MemberName);

        await server.ManageAsync(manager => manager.AssignRoleAsync(account, member.Id));

        await server.QueryAsync(async context =>
        {
            var row = await context.Accounts.SingleAsync(a => a.Id == account);
            context.Accounts.Remove(row);
            return await context.SaveChangesAsync();
        });

        // Cascaded rather than left pointing at nothing.
        var stranded = await server.QueryAsync(async context =>
            await context.AccountRoles.CountAsync(assignment => assignment.AccountId == account));

        Assert.Equal(0, stranded);
    }
}
