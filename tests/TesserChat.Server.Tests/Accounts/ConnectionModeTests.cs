using TesserChat.Server.Accounts;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Accounts;

/// <summary>
/// Covers the three connection modes (§5.2) as they gate registration.
/// </summary>
[Collection(ServerHostCollection.Name)]
public sealed class ConnectionModeTests(PostgresFixture postgres)
{
    private const string JoiningPassword = "let-me-in-please";

    // --- Open ------------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task Open_AdmitsAnyKey_WithNoCredential()
    {
        await using var server = await RegistrarHost.StartAsync(postgres, nameof(ConnectionMode.Open));
        using var identity = IdentityKeyPair.Generate();

        var result = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Anyone"));

        Assert.True(result.Succeeded);
    }

    [RequiresDockerFact]
    public async Task Open_IsTheDefault_WhenNoModeIsConfigured()
    {
        await using var server = await RegistrarHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();

        // A server that has said nothing about admission admits anyone — the documented default,
        // not an accident of the enum's zero value.
        Assert.True((await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Anyone"))).Succeeded);
    }

    [RequiresDockerFact]
    public async Task Open_IgnoresACredentialThatIsOffered()
    {
        await using var server = await RegistrarHost.StartAsync(postgres, nameof(ConnectionMode.Open));
        using var identity = IdentityKeyPair.Generate();

        // A client carrying a password from a previous configuration is admitted, not refused.
        var result = await server.InScopeAsync(registrar => registrar.RegisterAsync(
            identity.Public,
            "Anyone",
            new AdmissionCredentials(JoinSecret: "stale-password")));

        Assert.True(result.Succeeded);
    }

    // --- Password-gated --------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task PasswordGated_AdmitsTheCorrectPassword()
    {
        await using var server = await StartPasswordGatedAsync();
        using var identity = IdentityKeyPair.Generate();

        var result = await server.InScopeAsync(registrar => registrar.RegisterAsync(
            identity.Public,
            "Invited",
            new AdmissionCredentials(JoinSecret: JoiningPassword)));

        Assert.True(result.Succeeded);
    }

    [RequiresDockerFact]
    public async Task PasswordGated_RefusesAWrongOrMissingPassword()
    {
        await using var server = await StartPasswordGatedAsync();

        foreach (var credentials in new AdmissionCredentials?[]
                 {
                     null,
                     new AdmissionCredentials(),
                     new AdmissionCredentials(JoinSecret: ""),
                     new AdmissionCredentials(JoinSecret: "wrong"),
                     new AdmissionCredentials(JoinSecret: JoiningPassword + " "),
                 })
        {
            using var identity = IdentityKeyPair.Generate();

            var result = await server.InScopeAsync(registrar =>
                registrar.RegisterAsync(identity.Public, "Uninvited", credentials));

            Assert.Equal(AccountRegistrationStatus.NotPermitted, result.Status);
            Assert.Null(result.Account);
        }

        Assert.Equal(0, await server.CountAccountsAsync());
    }

    [RequiresDockerFact]
    public async Task PasswordGated_DoesNotAskAnExistingMemberAgain()
    {
        await using var server = await StartPasswordGatedAsync();
        using var identity = IdentityKeyPair.Generate();

        var joined = await server.InScopeAsync(registrar => registrar.RegisterAsync(
            identity.Public,
            "Member",
            new AdmissionCredentials(JoinSecret: JoiningPassword)));
        Assert.True(joined.Succeeded);

        // The password gates joining, not being a member (§5.2). A returning key presenting nothing
        // resolves to its existing account rather than being turned away.
        var returning = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Member"));

        Assert.True(returning.Succeeded);
        Assert.False(returning.IsNewAccount);
        Assert.Equal(joined.Account!.Id, returning.Account!.Id);
    }

    [RequiresDockerFact]
    public async Task PasswordGated_AdmitsNobody_WhenNoPasswordIsConfigured()
    {
        await using var server = await RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.PasswordGated));

        using var identity = IdentityKeyPair.Generate();

        // Fails closed. The alternative silently turns a restricted server open, which hands it to
        // whoever notices first.
        foreach (var credentials in new AdmissionCredentials?[]
                 {
                     null,
                     new AdmissionCredentials(JoinSecret: JoiningPassword),
                     new AdmissionCredentials(JoinSecret: ""),
                 })
        {
            var result = await server.InScopeAsync(registrar =>
                registrar.RegisterAsync(identity.Public, "Anyone", credentials));

            Assert.Equal(AccountRegistrationStatus.NotPermitted, result.Status);
        }
    }

    // --- Allowlist -------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task Allowlist_AdmitsAListedKey_AndRefusesAnUnlistedOne()
    {
        using var listed = IdentityKeyPair.Generate();
        using var unlisted = IdentityKeyPair.Generate();

        await using var server = await RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.AllowlistOnly),
            joinSecretHash: null,
            Encode(listed));

        Assert.True((await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(listed.Public, "Listed"))).Succeeded);

        var refused = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(unlisted.Public, "Unlisted"));

        Assert.Equal(AccountRegistrationStatus.NotPermitted, refused.Status);
        Assert.Equal(1, await server.CountAccountsAsync());
    }

    [RequiresDockerFact]
    public async Task Allowlist_AcceptsAFullShareableTokenAsWellAsABareKey()
    {
        using var bare = IdentityKeyPair.Generate();
        using var token = IdentityKeyPair.Generate();

        // An operator pastes whichever form the prospective member sent them.
        await using var server = await RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.AllowlistOnly),
            joinSecretHash: null,
            Encode(bare),
            token.Public.ToShareableString());

        Assert.True((await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(bare.Public, "Bare"))).Succeeded);
        Assert.True((await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(token.Public, "Token"))).Succeeded);
    }

    [RequiresDockerFact]
    public async Task Allowlist_IgnoresUnreadableEntries_RatherThanFailing()
    {
        using var listed = IdentityKeyPair.Generate();

        await using var server = await RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.AllowlistOnly),
            joinSecretHash: null,
            "not-base64!",
            "c2hvcnQ=",
            Encode(listed));

        // One typo in a long list must not take the server offline for everyone else on it.
        Assert.True((await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(listed.Public, "Listed"))).Succeeded);
    }

    [RequiresDockerFact]
    public async Task Allowlist_AdmitsNobody_WhenEmpty()
    {
        await using var server = await RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.AllowlistOnly));

        using var identity = IdentityKeyPair.Generate();

        var result = await server.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Anyone"));

        Assert.Equal(AccountRegistrationStatus.NotPermitted, result.Status);
    }

    [RequiresDockerFact]
    public async Task Allowlist_IsNotBypassedByAPassword()
    {
        using var unlisted = IdentityKeyPair.Generate();

        await using var server = await RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.AllowlistOnly),
            JoinSecretHasher.Hash(JoiningPassword),
            Encode(IdentityKeyPair.Generate().Public.SigningKey.ToArray()));

        // Only one policy is active at a time: a password means nothing on an allowlist server,
        // even one that has a hash configured from a previous mode.
        var result = await server.InScopeAsync(registrar => registrar.RegisterAsync(
            unlisted.Public,
            "Unlisted",
            new AdmissionCredentials(JoinSecret: JoiningPassword)));

        Assert.Equal(AccountRegistrationStatus.NotPermitted, result.Status);
    }

    [RequiresDockerFact]
    public async Task Allowlist_DoesNotRemoveAMemberWhoAlreadyJoined()
    {
        using var identity = IdentityKeyPair.Generate();

        await using var joined = await RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.AllowlistOnly),
            joinSecretHash: null,
            Encode(identity));

        Assert.True((await joined.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Member"))).Succeeded);

        // Removing a key from the list gates future registration; it is not a kick (§5.5). The
        // account is still there and still resolves.
        var stillThere = await joined.InScopeAsync(registrar => registrar.IsMemberAsync(identity.AccountId));
        Assert.True(stillThere);
    }

    // --- Rejection is uniform --------------------------------------------------------------

    [RequiresDockerFact]
    public async Task ARefusal_LooksTheSame_WhicheverModeRefused()
    {
        using var identity = IdentityKeyPair.Generate();

        await using var gated = await StartPasswordGatedAsync();
        await using var allowlisted = await RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.AllowlistOnly));

        var fromPassword = await gated.InScopeAsync(registrar => registrar.RegisterAsync(
            identity.Public,
            "Stranger",
            new AdmissionCredentials(JoinSecret: "wrong")));

        var fromAllowlist = await allowlisted.InScopeAsync(registrar =>
            registrar.RegisterAsync(identity.Public, "Stranger"));

        // An unauthenticated caller must not be able to tell a wrong password from an unlisted key
        // — that would disclose the server's mode, and probing the allowlist would enumerate it.
        Assert.Equal(AccountRegistrationStatus.NotPermitted, fromPassword.Status);
        Assert.Equal(fromPassword.Status, fromAllowlist.Status);
        Assert.Equal(fromPassword.Account, fromAllowlist.Account);
        Assert.Equal(fromPassword.IsNewAccount, fromAllowlist.IsNewAccount);
    }

    [RequiresDockerFact]
    public async Task ARefusedRegistration_WritesNothing()
    {
        await using var server = await StartPasswordGatedAsync();
        using var identity = IdentityKeyPair.Generate();

        await server.InScopeAsync(registrar => registrar.RegisterAsync(
            identity.Public,
            "Stranger",
            new AdmissionCredentials(JoinSecret: "wrong")));

        Assert.Equal(0, await server.CountAccountsAsync());
        Assert.False(await server.InScopeAsync(registrar => registrar.IsMemberAsync(identity.AccountId)));
    }

    private Task<RegistrarHost> StartPasswordGatedAsync()
        => RegistrarHost.StartAsync(
            postgres,
            nameof(ConnectionMode.PasswordGated),
            JoinSecretHasher.Hash(JoiningPassword));

    private static string Encode(IdentityKeyPair identity)
        => Encode(identity.Public.SigningKey.ToArray());

    private static string Encode(byte[] signingKey)
        => System.Buffers.Text.Base64Url.EncodeToString(signingKey);
}
