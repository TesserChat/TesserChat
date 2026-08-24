using System.Buffers.Text;
using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Authorization;
using TesserChat.Server.Persistence;
using TesserChat.Server.Setup;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Setup;

/// <summary>
/// Covers first-run setup and Owner assignment (§5.6).
/// </summary>
/// <remarks>
/// Setup is unauthenticated by necessity, so the refusals matter more here than the happy path.
/// The case the issue calls out as security-relevant — setup cannot be re-run to seize Owner — is
/// covered directly, and again after a restart.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class SetupServiceTests(PostgresFixture postgres)
{
    // --- Detecting first boot ----------------------------------------------------------------

    [RequiresDockerFact]
    public async Task AFreshServer_NeedsSetup()
    {
        await using var server = await SetupHost.StartAsync(postgres);

        Assert.True(await server.SetupAsync(setup => setup.IsSetupRequiredAsync()));
        Assert.Null(await server.GetServerAsync());
        Assert.Equal(0, await server.CountOwnersAsync());
    }

    [RequiresDockerFact]
    public async Task AConfiguredServer_DoesNotNeedSetup()
    {
        await using var server = await SetupHost.StartAsync(postgres);
        using var owner = IdentityKeyPair.Generate();

        Assert.True((await server.SetupAsync(setup =>
            setup.CompleteAsync(owner.Public, "Founder", "Test Server"))).Succeeded);

        Assert.False(await server.SetupAsync(setup => setup.IsSetupRequiredAsync()));
    }

    [RequiresDockerFact]
    public async Task SetupState_SurvivesARestart()
    {
        using var owner = IdentityKeyPair.Generate();
        string connectionString;

        await using (var first = await SetupHost.StartAsync(postgres))
        {
            connectionString = first.ConnectionString;
            Assert.True((await first.SetupAsync(setup =>
                setup.CompleteAsync(owner.Public, "Founder", "Test Server"))).Succeeded);
        }

        // A second host against the same database: the server must not re-enter setup, or every
        // restart would be a fresh chance to seize it.
        await using var restarted = await SetupHost.StartAgainstAsync(connectionString);

        Assert.False(await restarted.SetupAsync(setup => setup.IsSetupRequiredAsync()));
        Assert.Equal(1, await restarted.CountOwnersAsync());
    }

    // --- Completing setup --------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task CompletingSetup_RegistersTheOwnerAndNamesTheServer()
    {
        await using var server = await SetupHost.StartAsync(postgres);
        using var owner = IdentityKeyPair.Generate();

        var result = await server.SetupAsync(setup =>
            setup.CompleteAsync(owner.Public, "Founder", "My Community"));

        Assert.True(result.Succeeded);
        Assert.Equal(owner.AccountId, result.OwnerAccountId);
        Assert.NotEqual(Guid.Empty, result.ServerId);

        var stored = await server.GetServerAsync();
        Assert.NotNull(stored);
        Assert.Equal("My Community", stored.Name);
        Assert.Equal(result.ServerId, stored.Id);
        Assert.Equal(owner.AccountId, stored.SetUpByAccountId);

        // The account exists with both keys, exactly as ordinary registration would have made it.
        var account = await server.QueryAsync(async context =>
            await context.Accounts.SingleAsync(a => a.Id == owner.AccountId));

        Assert.Equal("Founder", account.DisplayName);
        Assert.Equal(owner.Public, account.ToPublicIdentity());
    }

    [RequiresDockerFact]
    public async Task CompletingSetup_AssignsExactlyOneOwner()
    {
        await using var server = await SetupHost.StartAsync(postgres);
        using var owner = IdentityKeyPair.Generate();

        await server.SetupAsync(setup => setup.CompleteAsync(owner.Public, "Founder", "Test Server"));

        Assert.Equal(1, await server.CountOwnersAsync());
        Assert.True(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(owner.AccountId)));

        // And the Owner's implicit authority applies immediately (§5.3.2).
        Assert.True(await server.ResolveAsync(resolver =>
            resolver.HasPermissionAsync(owner.AccountId, Permission.ServerManage)));
    }

    [RequiresDockerFact]
    public async Task TheServerName_FallsBackWhenNoneIsGiven()
    {
        await using var configured = await SetupHost.StartAsync(
            postgres,
            pinnedOwnerKey: null,
            serverName: "From Configuration");

        using var first = IdentityKeyPair.Generate();
        await configured.SetupAsync(setup => setup.CompleteAsync(first.Public, "Founder"));

        Assert.Equal("From Configuration", (await configured.GetServerAsync())!.Name);

        // With nothing configured and nothing supplied, a placeholder — a server is never nameless.
        await using var bare = await SetupHost.StartAsync(postgres);
        using var second = IdentityKeyPair.Generate();
        await bare.SetupAsync(setup => setup.CompleteAsync(second.Public, "Founder"));

        Assert.False(string.IsNullOrWhiteSpace((await bare.GetServerAsync())!.Name));
    }

    [RequiresDockerFact]
    public async Task CompletingSetup_RefusesABlankOrOverlongName()
    {
        await using var server = await SetupHost.StartAsync(postgres);
        using var owner = IdentityKeyPair.Generate();

        foreach (var name in new[] { "   ", new string('a', ServerInstance.NameMaxLength + 1) })
        {
            var result = await server.SetupAsync(setup =>
                setup.CompleteAsync(owner.Public, "Founder", name));

            Assert.Equal(SetupStatus.InvalidServerName, result.Status);
        }

        foreach (var displayName in new[] { "", "   " })
        {
            var result = await server.SetupAsync(setup =>
                setup.CompleteAsync(owner.Public, displayName, "Test Server"));

            Assert.Equal(SetupStatus.InvalidDisplayName, result.Status);
        }

        // Nothing was written by any of the refusals.
        Assert.True(await server.SetupAsync(setup => setup.IsSetupRequiredAsync()));
        Assert.Equal(0, await server.CountOwnersAsync());
    }

    // --- Setup cannot be re-run (the security case) ------------------------------------------

    [RequiresDockerFact]
    public async Task SetupCannotBeReRun_ToSeizeOwnership()
    {
        await using var server = await SetupHost.StartAsync(postgres);
        using var founder = IdentityKeyPair.Generate();
        using var attacker = IdentityKeyPair.Generate();

        await server.SetupAsync(setup => setup.CompleteAsync(founder.Public, "Founder", "Test Server"));

        // The security-relevant case: setup is unauthenticated, so a configured server must refuse
        // it outright or it becomes an unauthenticated route to Owner on a live server.
        var seized = await server.SetupAsync(setup =>
            setup.CompleteAsync(attacker.Public, "Attacker", "Seized"));

        Assert.Equal(SetupStatus.AlreadyConfigured, seized.Status);

        // Nothing about the server changed: same Owner, same name, no new account.
        Assert.Equal(1, await server.CountOwnersAsync());
        Assert.True(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(founder.AccountId)));
        Assert.False(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(attacker.AccountId)));
        Assert.Equal("Test Server", (await server.GetServerAsync())!.Name);

        var attackerExists = await server.QueryAsync(async context =>
            await context.Accounts.AnyAsync(a => a.Id == attacker.AccountId));
        Assert.False(attackerExists);
    }

    [RequiresDockerFact]
    public async Task SetupCannotBeReRun_EvenByTheOriginalOwner()
    {
        await using var server = await SetupHost.StartAsync(postgres);
        using var founder = IdentityKeyPair.Generate();

        await server.SetupAsync(setup => setup.CompleteAsync(founder.Public, "Founder", "Test Server"));

        // Not a permission anyone holds, the Owner included — setup is a one-time event, not an
        // administrative action.
        var again = await server.SetupAsync(setup =>
            setup.CompleteAsync(founder.Public, "Founder", "Renamed"));

        Assert.Equal(SetupStatus.AlreadyConfigured, again.Status);
        Assert.Equal("Test Server", (await server.GetServerAsync())!.Name);
    }

    [RequiresDockerFact]
    public async Task SetupCannotBeReRun_AfterARestart()
    {
        using var founder = IdentityKeyPair.Generate();
        using var attacker = IdentityKeyPair.Generate();
        string connectionString;

        await using (var first = await SetupHost.StartAsync(postgres))
        {
            connectionString = first.ConnectionString;
            await first.SetupAsync(setup =>
                setup.CompleteAsync(founder.Public, "Founder", "Test Server"));
        }

        await using var restarted = await SetupHost.StartAgainstAsync(connectionString);

        var seized = await restarted.SetupAsync(setup =>
            setup.CompleteAsync(attacker.Public, "Attacker", "Seized"));

        Assert.Equal(SetupStatus.AlreadyConfigured, seized.Status);
        Assert.True(await restarted.ResolveAsync(resolver => resolver.IsOwnerAsync(founder.AccountId)));
    }

    // --- Pinned owner key --------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task APinnedKey_IsTheOnlyOneThatCanClaimOwnership()
    {
        using var pinned = IdentityKeyPair.Generate();
        using var other = IdentityKeyPair.Generate();

        await using var server = await SetupHost.StartAsync(postgres, Encode(pinned));

        var refused = await server.SetupAsync(setup =>
            setup.CompleteAsync(other.Public, "Impostor", "Test Server"));

        Assert.Equal(SetupStatus.NotThePinnedOwner, refused.Status);
        Assert.True(await server.SetupAsync(setup => setup.IsSetupRequiredAsync()));

        // The pinned key still can, and setup is still pending for it — a refused attempt must not
        // consume the one chance to set the server up.
        var accepted = await server.SetupAsync(setup =>
            setup.CompleteAsync(pinned.Public, "Founder", "Test Server"));

        Assert.True(accepted.Succeeded);
        Assert.True(await server.ResolveAsync(resolver => resolver.IsOwnerAsync(pinned.AccountId)));
    }

    [RequiresDockerFact]
    public async Task APinnedKey_IsAcceptedAsAShareableTokenToo()
    {
        using var pinned = IdentityKeyPair.Generate();

        // An operator pastes whichever form they have to hand.
        await using var server = await SetupHost.StartAsync(postgres, pinned.Public.ToShareableString());

        var result = await server.SetupAsync(setup =>
            setup.CompleteAsync(pinned.Public, "Founder", "Test Server"));

        Assert.True(result.Succeeded);
    }

    [RequiresDockerFact]
    public async Task AnUnreadablePinnedKey_RefusesEveryone()
    {
        using var identity = IdentityKeyPair.Generate();

        await using var server = await SetupHost.StartAsync(postgres, "not-a-key!");

        // Fails closed. An operator who pinned a key meant to restrict setup, and a value that
        // cannot be read is not evidence they changed their mind.
        var result = await server.SetupAsync(setup =>
            setup.CompleteAsync(identity.Public, "Anyone", "Test Server"));

        Assert.Equal(SetupStatus.NotThePinnedOwner, result.Status);
        Assert.True(await server.SetupAsync(setup => setup.IsSetupRequiredAsync()));
    }

    [RequiresDockerFact]
    public async Task WithNoPinnedKey_TheFirstClientWins()
    {
        await using var server = await SetupHost.StartAsync(postgres);
        using var first = IdentityKeyPair.Generate();

        // The documented fallback: fine on a machine nothing else can reach, which is why the
        // server logs a warning while it applies.
        Assert.True((await server.SetupAsync(setup =>
            setup.CompleteAsync(first.Public, "Founder", "Test Server"))).Succeeded);
    }

    // --- Concurrency -------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task ConcurrentSetup_LeavesExactlyOneOwner()
    {
        await using var server = await SetupHost.StartAsync(postgres);

        var claimants = Enumerable.Range(0, 6).Select(_ => IdentityKeyPair.Generate()).ToList();

        try
        {
            var attempts = await Task.WhenAll(claimants.Select((identity, i) =>
                server.SetupAsync(setup =>
                    setup.CompleteAsync(identity.Public, $"Claimant {i}", "Test Server"))));

            // The single-row constraint decides it: one winner, everyone else told the server is
            // already configured.
            Assert.Single(attempts, attempt => attempt.Succeeded);
            Assert.All(
                attempts.Where(attempt => !attempt.Succeeded),
                attempt => Assert.Equal(SetupStatus.AlreadyConfigured, attempt.Status));

            Assert.Equal(1, await server.CountOwnersAsync());

            var servers = await server.QueryAsync(async context =>
                await context.ServerInstances.CountAsync());
            Assert.Equal(1, servers);
        }
        finally
        {
            foreach (var identity in claimants)
            {
                identity.Dispose();
            }
        }
    }

    [RequiresDockerFact]
    public async Task ASecondServerRow_CannotBeInserted()
    {
        await using var server = await SetupHost.StartAsync(postgres);
        using var owner = IdentityKeyPair.Generate();

        await server.SetupAsync(setup => setup.CompleteAsync(owner.Public, "Founder", "Test Server"));

        // Straight at the database, to prove the constraint is what holds the invariant rather than
        // the code path that happens to check first.
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await server.QueryAsync(async context =>
            {
                context.ServerInstances.Add(new ServerInstance
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    Name = "Second",
                    SetUpAt = DateTimeOffset.UtcNow,
                    SetUpByAccountId = owner.AccountId,
                });

                return await context.SaveChangesAsync();
            }));
    }

    private static string Encode(IdentityKeyPair identity)
        => Base64Url.EncodeToString(identity.Public.SigningKey.ToArray());
}
