using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Auditing;
using TesserChat.Server.Authorization;
using TesserChat.Server.Persistence;
using TesserChat.Server.Tests.Authorization;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Auditing;

/// <summary>
/// Covers the audit log (§5.5): what gets recorded, and that it cannot be rewritten.
/// </summary>
/// <remarks>
/// The append-only guarantee is a property of the database, so these assert it against a real
/// Postgres by attempting the writes directly. An in-memory provider has no rules and would pass
/// whether or not the migration created any.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class AuditLogTests(PostgresFixture postgres)
{
    // --- Append-only ---------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task AnAuditEntry_CannotBeUpdated()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Moderator");

        await server.ManageAsync(manager => manager.CreateRoleAsync("Moderator", actor));

        var original = await server.QueryAsync(async context =>
            await context.AuditEntries.AsNoTracking().SingleAsync());

        // Straight at the database, bypassing anything the service layer would refuse.
        await server.QueryAsync(async context => await context.Database.ExecuteSqlRawAsync(
            "UPDATE audit_entries SET detail = 'nothing happened here'"));

        var after = await server.QueryAsync(async context =>
            await context.AuditEntries.AsNoTracking().SingleAsync());

        // The rule rewrites the statement to nothing, so the write silently does not happen.
        Assert.Equal(original.Detail, after.Detail);
        Assert.DoesNotContain("nothing happened here", after.Detail, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task AnAuditEntry_CannotBeDeleted()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Moderator");

        await server.ManageAsync(manager => manager.CreateRoleAsync("Moderator", actor));

        var before = await server.QueryAsync(async context => await context.AuditEntries.CountAsync());
        Assert.Equal(1, before);

        await server.QueryAsync(async context =>
            await context.Database.ExecuteSqlRawAsync("DELETE FROM audit_entries"));

        var after = await server.QueryAsync(async context => await context.AuditEntries.CountAsync());

        // An audit log a moderator can quietly empty is not an audit log.
        Assert.Equal(1, after);
    }

    [RequiresDockerFact]
    public async Task DeletingAnAccount_DoesNotRemoveWhatItDid()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Departing");

        await server.ManageAsync(manager => manager.CreateRoleAsync("Moderator", actor));

        await server.QueryAsync(async context =>
        {
            var account = await context.Accounts.SingleAsync(a => a.Id == actor);
            context.Accounts.Remove(account);
            return await context.SaveChangesAsync();
        });

        var entries = await server.QueryAsync(async context =>
            await context.AuditEntries.AsNoTracking().ToListAsync());

        // Every other join cascades on account deletion; this one must not, or deleting an account
        // would erase the record of what it did.
        Assert.Single(entries);
        Assert.Equal(actor, entries[0].ActorAccountId);
    }

    // --- What gets recorded --------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task RoleLifecycleActions_AreEachRecorded()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Admin");
        var subject = await server.RegisterAccountAsync("Member");

        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Moderator", actor));
        await server.ManageAsync(manager =>
            manager.SetPermissionsAsync(role!.Id, [Permission.MessagesDelete.Key], actor));
        await server.ManageAsync(manager => manager.RenameRoleAsync(role!.Id, "Sweeper", actor));
        await server.ManageAsync(manager => manager.AssignRoleAsync(subject, role!.Id, actor));
        await server.ManageAsync(manager => manager.RevokeRoleAsync(subject, role!.Id, actor));
        await server.ManageAsync(manager => manager.DeleteRoleAsync(role!.Id, actor));

        var actions = await server.QueryAsync(async context => await context.AuditEntries
            .AsNoTracking()
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.Action)
            .ToListAsync());

        Assert.Equal(
            [
                AuditAction.RoleCreated,
                AuditAction.RolePermissionsChanged,
                AuditAction.RoleRenamed,
                AuditAction.RoleGranted,
                AuditAction.RoleRevoked,
                AuditAction.RoleDeleted,
            ],
            actions);
    }

    [RequiresDockerFact]
    public async Task AnEntry_RecordsWhoDidItAndToWhom()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Admin");
        var subject = await server.RegisterAccountAsync("Member");

        var admin = await server.GetRoleAsync(SystemRoles.AdminName);
        await server.ManageAsync(manager => manager.AssignRoleAsync(subject, admin.Id, actor));

        var entry = await server.QueryAsync(async context => await context.AuditEntries
            .AsNoTracking()
            .SingleAsync(e => e.Action == AuditAction.RoleGranted));

        Assert.Equal(actor, entry.ActorAccountId);
        Assert.Equal(subject, entry.TargetAccountId);
        Assert.Equal(admin.Id, entry.TargetRoleId);
        Assert.Contains(SystemRoles.AdminName, entry.Detail, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task ADetail_StaysReadableAfterTheRoleIsGone()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Admin");

        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Ephemeral", actor));
        await server.ManageAsync(manager => manager.DeleteRoleAsync(role!.Id, actor));

        var entry = await server.QueryAsync(async context => await context.AuditEntries
            .AsNoTracking()
            .SingleAsync(e => e.Action == AuditAction.RoleDeleted));

        // The role id is recorded but no longer resolves. The name in the detail is what keeps the
        // entry meaningful.
        Assert.Contains("Ephemeral", entry.Detail, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task APermissionChange_RecordsWhatChangedRatherThanTheResultingSet()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Admin");

        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Custom", actor));

        await server.ManageAsync(manager => manager.SetPermissionsAsync(
            role!.Id,
            [Permission.MembersKick.Key, Permission.MembersBan.Key],
            actor));

        await server.ManageAsync(manager => manager.SetPermissionsAsync(
            role!.Id,
            [Permission.MembersBan.Key],
            actor));

        var details = await server.QueryAsync(async context => await context.AuditEntries
            .AsNoTracking()
            .Where(e => e.Action == AuditAction.RolePermissionsChanged)
            .OrderBy(e => e.Id)
            .Select(e => e.Detail)
            .ToListAsync());

        Assert.Equal(2, details.Count);
        Assert.Contains("Granted", details[0], StringComparison.Ordinal);
        Assert.Contains(Permission.MembersKick.Key, details[0], StringComparison.Ordinal);

        // The second change withdrew one permission — that is the auditable fact.
        Assert.Contains("Withdrew", details[1], StringComparison.Ordinal);
        Assert.Contains(Permission.MembersKick.Key, details[1], StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task ANoOpMutation_RecordsNothing()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Admin");
        var subject = await server.RegisterAccountAsync("Member");

        var admin = await server.GetRoleAsync(SystemRoles.AdminName);

        await server.ManageAsync(manager => manager.AssignRoleAsync(subject, admin.Id, actor));
        var before = await server.QueryAsync(async context => await context.AuditEntries.CountAsync());

        // Granting a role already held, and renaming to the name it already has, change nothing —
        // so they must not appear in the trail as though they had.
        await server.ManageAsync(manager => manager.AssignRoleAsync(subject, admin.Id, actor));
        await server.ManageAsync(manager =>
            manager.RenameRoleAsync(admin.Id, SystemRoles.AdminName, actor));

        var after = await server.QueryAsync(async context => await context.AuditEntries.CountAsync());

        Assert.Equal(before, after);
    }

    [RequiresDockerFact]
    public async Task ARefusedMutation_RecordsNothing()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Admin");
        var founder = await server.RegisterAccountAsync("Founder");

        var owner = await server.GetRoleAsync(SystemRoles.OwnerName);
        await server.ManageAsync(manager => manager.AssignRoleAsync(founder, owner.Id, actor));

        var before = await server.QueryAsync(async context => await context.AuditEntries.CountAsync());

        // Refused: it would remove the last Owner. Nothing happened, so nothing is recorded — the
        // entry shares the transaction with the change, and there was no change.
        var refused = await server.ManageAsync(manager =>
            manager.RevokeRoleAsync(founder, owner.Id, actor));
        Assert.False(refused.Succeeded);

        var deleteRefused = await server.ManageAsync(manager =>
            manager.DeleteRoleAsync(owner.Id, actor));
        Assert.False(deleteRefused.Succeeded);

        var after = await server.QueryAsync(async context => await context.AuditEntries.CountAsync());

        Assert.Equal(before, after);
    }

    [RequiresDockerFact]
    public async Task AnActionWithNoActor_IsRecordedWithoutOne()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);

        var (_, role) = await server.ManageAsync(manager => manager.CreateRoleAsync("Unattributed"));

        var entry = await server.QueryAsync(async context => await context.AuditEntries
            .AsNoTracking()
            .SingleAsync(e => e.Action == AuditAction.RoleCreated));

        // Null means "the server did this", not "we do not know who did".
        Assert.Null(entry.ActorAccountId);
        Assert.Equal(role!.Id, entry.TargetRoleId);
    }

    // --- Reading -------------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task ReadReturnsNewestFirst_AndPagesBackwards()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Admin");

        for (var i = 0; i < 5; i++)
        {
            await server.ManageAsync(manager => manager.CreateRoleAsync($"Role {i}", actor));
        }

        var newest = await server.AuditAsync(log => log.ReadAsync(limit: 2));

        Assert.Equal(2, newest.Count);
        Assert.True(newest[0].Id > newest[1].Id);
        Assert.Contains("Role 4", newest[0].Detail, StringComparison.Ordinal);

        var older = await server.AuditAsync(log => log.ReadAsync(limit: 2, before: newest[1].Id));

        Assert.Equal(2, older.Count);
        Assert.True(older[0].Id < newest[1].Id);
        Assert.Contains("Role 2", older[0].Detail, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task ReadForAccount_FindsAnAccountAsActorAndAsTarget()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var moderator = await server.RegisterAccountAsync("Moderator");
        var member = await server.RegisterAccountAsync("Member");

        var admin = await server.GetRoleAsync(SystemRoles.AdminName);

        // The moderator acts once, and is acted upon once.
        await server.ManageAsync(manager => manager.AssignRoleAsync(member, admin.Id, moderator));
        await server.ManageAsync(manager => manager.AssignRoleAsync(moderator, admin.Id, member));

        var forModerator = await server.AuditAsync(log => log.ReadForAccountAsync(moderator));

        // Both questions an audit log is asked: what did they do, and what was done to them.
        Assert.Equal(2, forModerator.Count);
        Assert.Contains(forModerator, e => e.ActorAccountId == moderator);
        Assert.Contains(forModerator, e => e.TargetAccountId == moderator);
    }

    [RequiresDockerFact]
    public async Task ReadCapsAnOversizedLimit()
    {
        await using var server = await AuthorizationHost.StartAsync(postgres);
        var actor = await server.RegisterAccountAsync("Admin");

        await server.ManageAsync(manager => manager.CreateRoleAsync("Only", actor));

        // The log only grows, so an uncapped read gets slower every day it runs.
        var entries = await server.AuditAsync(log => log.ReadAsync(limit: int.MaxValue));

        Assert.Single(entries);
    }

    // --- Setup ---------------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task CompletingSetup_IsRecordedWithTheOwnerAsTarget()
    {
        await using var server = await Setup.SetupHost.StartAsync(postgres);
        using var owner = IdentityKeyPair.Generate();

        await server.SetupAsync(setup => setup.CompleteAsync(owner.Public, "Founder", "Test Server"));

        var entry = await server.QueryAsync(async context => await context.AuditEntries
            .AsNoTracking()
            .SingleAsync(e => e.Action == AuditAction.ServerSetUp));

        // No actor: setup runs before any account exists to attribute it to.
        Assert.Null(entry.ActorAccountId);
        Assert.Equal(owner.AccountId, entry.TargetAccountId);
        Assert.Equal(SystemRoles.OwnerId, entry.TargetRoleId);
        Assert.Contains("Test Server", entry.Detail, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task ARefusedSetup_RecordsNothing()
    {
        using var pinned = IdentityKeyPair.Generate();
        using var other = IdentityKeyPair.Generate();

        await using var server = await Setup.SetupHost.StartAsync(
            postgres,
            System.Buffers.Text.Base64Url.EncodeToString(pinned.Public.SigningKey.ToArray()));

        await server.SetupAsync(setup => setup.CompleteAsync(other.Public, "Impostor", "Seized"));

        var count = await server.QueryAsync(async context => await context.AuditEntries.CountAsync());

        Assert.Equal(0, count);
    }
}
