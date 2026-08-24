using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Accounts;
using TesserChat.Server.Persistence;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Accounts;

/// <summary>
/// Covers account registration and public key storage against a real PostgreSQL (§5.1).
/// </summary>
/// <remarks>
/// Against a real database rather than a fake, because the properties under test here — the unique
/// index holding for a repeat key, a fixed-length <c>bytea</c> round-tripping intact — are Postgres
/// behaviour. An in-memory provider would pass whether or not the schema said any of it (§5.4).
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class AccountRegistrarTests(PostgresFixture postgres)
{
    [RequiresDockerFact]
    public async Task Register_StoresBothPublicKeys_AndDerivesTheAccountId()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        var result = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Ada"));

        Assert.True(result.Succeeded);
        Assert.True(result.IsNewAccount);

        var stored = await server.InScopeAsync(registrar => registrar.FindAsync(identity.AccountId));

        Assert.NotNull(stored);
        Assert.Equal(identity.AccountId, stored.Id);
        Assert.Equal(identity.Public.SigningKey.ToArray(), stored.SigningKey);
        Assert.Equal(identity.Public.EncryptionKey.ToArray(), stored.EncryptionKey);
        Assert.Equal("Ada", stored.DisplayName);

        // The stored keys must rebuild the identity that registered — proof the two columns were
        // not swapped, which nothing else here would catch since both are 32 bytes.
        Assert.Equal(identity.Public, stored.ToPublicIdentity());
    }

    [RequiresDockerFact]
    public async Task Register_ResolvesToTheSameAccount_WhenTheSameKeyRegistersTwice()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        var first = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "First"));
        var second = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Second"));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);

        Assert.True(first.IsNewAccount);
        Assert.False(second.IsNewAccount);

        Assert.Equal(first.Account!.Id, second.Account!.Id);

        // One row, not two: the id derives from the key, so a repeat registration cannot fork the
        // account (§5.1).
        Assert.Equal(1, await server.CountAccountsAsync());

        // Re-registering is not a rename. The returning member keeps the name they last set.
        Assert.Equal("First", second.Account.DisplayName);
    }

    [RequiresDockerFact]
    public async Task Register_KeepsAccountsDistinct_ForDifferentKeys()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var first = IdentityKeyPair.Generate();
        using var second = IdentityKeyPair.Generate();

        await server.InScopeAsync(registrar => registrar.RegisterAsync(first.Public, "Same Name"));
        await server.InScopeAsync(registrar => registrar.RegisterAsync(second.Public, "Same Name"));

        // Display names collide freely; identity is the key, never the name.
        Assert.Equal(2, await server.CountAccountsAsync());
        Assert.NotEqual(first.AccountId, second.AccountId);
    }

    [RequiresDockerFact]
    public async Task SetDisplayName_ChangesTheName_ButNotTheAccountId()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        await server.InScopeAsync(registrar => registrar.RegisterAsync(identity.Public, "Before"));

        var changed = await server.InScopeAsync(registrar =>
            registrar.TrySetDisplayNameAsync(identity.AccountId, "After"));

        Assert.True(changed);

        var stored = await server.InScopeAsync(registrar => registrar.FindAsync(identity.AccountId));

        Assert.NotNull(stored);
        Assert.Equal("After", stored.DisplayName);
        Assert.Equal(identity.AccountId, stored.Id);
        Assert.Equal(identity.Public.SigningKey.ToArray(), stored.SigningKey);
    }

    [RequiresDockerFact]
    public async Task SetDisplayName_Fails_ForAnAccountThatIsNotRegistered()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);

        var changed = await server.InScopeAsync(registrar =>
            registrar.TrySetDisplayNameAsync(Guid.NewGuid(), "Nobody"));

        Assert.False(changed);
    }

    [RequiresDockerFact]
    public async Task Register_RejectsABlankDisplayName()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);

        foreach (var displayName in new[] { "", "   ", "\t\n" })
        {
            using var identity = IdentityKeyPair.Generate();

            var result = await server.InScopeAsync(registrar =>
                registrar.RegisterAsync(identity.Public, displayName));

            Assert.Equal(AccountRegistrationStatus.InvalidDisplayName, result.Status);
            Assert.Null(result.Account);
        }

        Assert.Equal(0, await server.CountAccountsAsync());
    }

    [RequiresDockerFact]
    public async Task Register_RejectsADisplayNameOverTheLimit()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        var tooLong = new string('a', Account.DisplayNameMaxLength + 1);

        var result = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, tooLong));

        Assert.Equal(AccountRegistrationStatus.InvalidDisplayName, result.Status);

        // Rejected before the insert, so nothing was written — a name Postgres would refuse must
        // never reach the column.
        Assert.Equal(0, await server.CountAccountsAsync());
    }

    [RequiresDockerFact]
    public async Task Register_AcceptsADisplayNameExactlyAtTheLimit()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        var atLimit = new string('a', Account.DisplayNameMaxLength);

        var result = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, atLimit));

        Assert.True(result.Succeeded);
        Assert.Equal(atLimit, result.Account!.DisplayName);
    }

    [RequiresDockerFact]
    public async Task Register_TrimsSurroundingWhitespaceFromTheDisplayName()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        var result = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "  Grace  "));

        Assert.True(result.Succeeded);
        Assert.Equal("Grace", result.Account!.DisplayName);
    }

    [RequiresDockerFact]
    public async Task FindBySigningKey_ResolvesARegisteredAccount()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        await server.InScopeAsync(registrar => registrar.RegisterAsync(identity.Public, "Ada"));

        var found = await server.InScopeAsync(registrar =>
            registrar.FindBySigningKeyAsync(identity.Public.SigningKey.ToArray()));

        Assert.NotNull(found);
        Assert.Equal(identity.AccountId, found.Id);
    }

    [RequiresDockerFact]
    public async Task FindBySigningKey_ReturnsNothing_ForAnUnregisteredKey()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        var found = await server.InScopeAsync(registrar =>
            registrar.FindBySigningKeyAsync(identity.Public.SigningKey.ToArray()));

        Assert.Null(found);
    }

    [RequiresDockerFact]
    public async Task FindBySigningKey_ReturnsNothing_ForAMalformedKey()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);

        // A wrong-length key arrives over the wire and is not trusted to be well formed, so this
        // resolves to nothing rather than throwing.
        foreach (var length in new[] { 0, 1, 31, 33, 64 })
        {
            var found = await server.InScopeAsync(registrar =>
                registrar.FindBySigningKeyAsync(new byte[length]));

            Assert.Null(found);
        }
    }

    [RequiresDockerFact]
    public async Task IsMember_TracksWhetherTheAccountIsRegisteredHere()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var member = IdentityKeyPair.Generate();
        using var stranger = IdentityKeyPair.Generate();

        await server.InScopeAsync(registrar => registrar.RegisterAsync(member.Public, "Member"));

        // The check §7.4.1 makes before accepting a relayed or queued direct message.
        Assert.True(await server.InScopeAsync(registrar => registrar.IsMemberAsync(member.AccountId)));
        Assert.False(await server.InScopeAsync(registrar => registrar.IsMemberAsync(stranger.AccountId)));
    }

    [RequiresDockerFact]
    public async Task Register_ResolvesToOneAccount_WhenTheSameKeyRegistersConcurrently()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        // Separate scopes, so each attempt has its own DbContext and they genuinely race at the
        // database rather than inside one change tracker.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            server.InScopeAsync(registrar => registrar.RegisterAsync(identity.Public, $"Racer {i}"))));

        Assert.All(attempts, attempt => Assert.True(attempt.Succeeded));

        // Exactly one attempt created the account; the losers report the winner's row rather than
        // failing on the unique index.
        Assert.Single(attempts, attempt => attempt.IsNewAccount);
        Assert.All(attempts, attempt => Assert.Equal(identity.AccountId, attempt.Account!.Id));

        Assert.Equal(1, await server.CountAccountsAsync());
    }

    [RequiresDockerFact]
    public async Task Register_StampsRegisteredAt_FromTheClock()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        var before = DateTimeOffset.UtcNow;
        var result = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Ada"));
        var after = DateTimeOffset.UtcNow;

        Assert.True(result.Succeeded);

        var stored = await server.InScopeAsync(registrar => registrar.FindAsync(identity.AccountId));

        Assert.NotNull(stored);

        // A second of slack either way: timestamptz rounds to microseconds and the comparison
        // spans a database round-trip.
        Assert.InRange(stored.RegisteredAt, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [RequiresDockerFact]
    public async Task Accounts_AreScopedToOneServer_NotSharedAcrossDatabases()
    {
        using var identity = IdentityKeyPair.Generate();

        await using var first = await RegistrarHost.StartAsync(postgres);
        await using var second = await RegistrarHost.StartAsync(postgres);

        await first.InScopeAsync(registrar => registrar.RegisterAsync(identity.Public, "Ada"));

        // Registration is per-server: there is no global account system (§5.1), so the same key is
        // a stranger on a server it has not joined — while still deriving the same id there.
        Assert.True(await first.InScopeAsync(registrar => registrar.IsMemberAsync(identity.AccountId)));
        Assert.False(await second.InScopeAsync(registrar => registrar.IsMemberAsync(identity.AccountId)));
    }
}
