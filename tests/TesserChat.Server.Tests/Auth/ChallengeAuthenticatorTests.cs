using TesserChat.Server.Auth;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Auth;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Auth;

/// <summary>
/// Covers challenge-response login (§4.7).
/// </summary>
/// <remarks>
/// The negative cases are the substance here (§0.1), and one of them is the reason the flow exists:
/// a signature made for one server must not authenticate on another. That case is covered against
/// two genuinely separate servers rather than by substituting an id, so it tests the property
/// end to end.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class ChallengeAuthenticatorTests(PostgresFixture postgres)
{
    // --- Issuing a challenge -------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task IssuingAChallenge_ReturnsTheServerIdAndAnUnspentNonce()
    {
        await using var server = await LoginHost.StartAsync(postgres);

        var issued = await server.IssueAsync();

        Assert.Equal(server.ServerId, issued.ServerId);
        Assert.Equal(LoginChallenge.NonceSize, issued.Nonce.Length);

        var stored = await server.FindNonceAsync(issued.Nonce);
        Assert.NotNull(stored);
        Assert.Null(stored.ConsumedAt);

        // Not exact equality: Postgres keeps timestamptz to the microsecond while DateTimeOffset
        // counts 100ns ticks, so the stored expiry is a sub-microsecond truncation of the one the
        // client was told. The enforced deadline is the stored one, and it is never later than the
        // advertised one — which is the direction that matters.
        Assert.InRange(
            stored.ExpiresAt,
            issued.ExpiresAt - TimeSpan.FromMicroseconds(1),
            issued.ExpiresAt);

        Assert.True(stored.ExpiresAt > stored.IssuedAt);
    }

    [RequiresDockerFact]
    public async Task EveryChallenge_IsDifferent()
    {
        await using var server = await LoginHost.StartAsync(postgres);

        var first = await server.IssueAsync();
        var second = await server.IssueAsync();

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.Equal(2, await server.CountNoncesAsync());
    }

    [RequiresDockerFact]
    public async Task AnUnconfiguredServer_IssuesNoChallenge()
    {
        // Nothing to bind a signature to until setup writes the server's id, so there is no
        // meaningful challenge to hand out. The way in is setup (§5.6), not login.
        await using var server = await LoginHost.StartAsync(postgres, completeSetup: false);

        Assert.Null(await server.AuthAsync(auth => auth.IssueChallengeAsync()));
    }

    [RequiresDockerFact]
    public async Task AnUnconfiguredServer_AuthenticatesNobody()
    {
        await using var server = await LoginHost.StartAsync(postgres, completeSetup: false);
        using var identity = IdentityKeyPair.Generate();

        var nonce = new byte[LoginChallenge.NonceSize];
        var signature = LoginChallenge.Sign(identity, Guid.NewGuid(), nonce);

        var result = await server.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), nonce, signature));

        Assert.Equal(LoginStatus.ServerNotConfigured, result.Status);
    }

    // --- The happy path ------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task AValidSignature_AuthenticatesTheAccount()
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity, "Ada");

        var issued = await server.IssueAsync();
        var signature = LoginChallenge.Sign(identity, issued.ServerId, issued.Nonce);

        var result = await server.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), issued.Nonce, signature));

        Assert.True(result.Succeeded);
        Assert.Equal(identity.AccountId, result.AccountId);
    }

    [RequiresDockerFact]
    public async Task ASuccessfulLogin_SpendsTheChallenge()
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var issued = await server.IssueAsync();
        var signature = LoginChallenge.Sign(identity, issued.ServerId, issued.Nonce);

        Assert.True((await server.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), issued.Nonce, signature))).Succeeded);

        var stored = await server.FindNonceAsync(issued.Nonce);
        Assert.NotNull(stored);
        Assert.NotNull(stored.ConsumedAt);
    }

    // --- Replay across servers: the property the whole flow exists for -------------------------

    [RequiresDockerFact]
    public async Task ASignatureForOneServer_IsRejectedByAnother()
    {
        // Two real servers, each with its own id and its own database — the exact situation a
        // self-hosted network creates. A malicious server collects a signature from a client and
        // presents it to a server the client actually has an account on.
        await using var attacker = await LoginHost.StartAsync(postgres);
        await using var victim = await LoginHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        await victim.RegisterAsync(identity);

        Assert.NotEqual(attacker.ServerId, victim.ServerId);

        // Signed for the attacker's server. Its nonce is unknown to the victim, so the victim is
        // also given one of its own to replay against — otherwise this would only prove that an
        // unknown nonce is refused, which is a different (and weaker) test.
        var attackerIssued = await attacker.IssueAsync();
        var stolenSignature = LoginChallenge.Sign(identity, attackerIssued.ServerId, attackerIssued.Nonce);

        var victimIssued = await victim.IssueAsync();

        // Both replays fail, and for the same reason: the payload the victim verifies over carries
        // the victim's id, so nothing signed elsewhere can match it.
        var withStolenNonce = await victim.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), attackerIssued.Nonce, stolenSignature));
        Assert.Equal(LoginStatus.UnknownOrSpentChallenge, withStolenNonce.Status);

        var withVictimNonce = await victim.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), victimIssued.Nonce, stolenSignature));
        Assert.Equal(LoginStatus.InvalidSignature, withVictimNonce.Status);
    }

    [RequiresDockerFact]
    public async Task ASignatureBoundToAnotherServerId_DoesNotVerify()
    {
        // The same property at the payload level: same nonce, same key, different target.
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var issued = await server.IssueAsync();
        var wrongTarget = LoginChallenge.Sign(identity, Guid.NewGuid(), issued.Nonce);

        var result = await server.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), issued.Nonce, wrongTarget));

        Assert.Equal(LoginStatus.InvalidSignature, result.Status);
    }

    // --- Single use ----------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task AReusedChallenge_IsRejectedEvenWithinItsLifetime()
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var issued = await server.IssueAsync();
        var signature = LoginChallenge.Sign(identity, issued.ServerId, issued.Nonce);
        var key = identity.Public.SigningKey.ToArray();

        Assert.True((await server.AuthAsync(auth =>
            auth.LoginAsync(key, issued.Nonce, signature))).Succeeded);

        // Same nonce, same valid signature, still inside the lifetime. Nothing about the signature
        // changed — only that this challenge has already been spent.
        var replay = await server.AuthAsync(auth => auth.LoginAsync(key, issued.Nonce, signature));

        Assert.Equal(LoginStatus.UnknownOrSpentChallenge, replay.Status);
    }

    [RequiresDockerFact]
    public async Task ConcurrentPresentations_OfOneChallenge_YieldExactlyOneLogin()
    {
        // The case a read-then-write implementation passes every sequential test and still gets
        // wrong. Consuming is a conditional UPDATE, so Postgres decides the winner.
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var issued = await server.IssueAsync();
        var signature = LoginChallenge.Sign(identity, issued.ServerId, issued.Nonce);
        var key = identity.Public.SigningKey.ToArray();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            server.AuthAsync(auth => auth.LoginAsync(key, issued.Nonce, signature))));

        Assert.Equal(1, attempts.Count(attempt => attempt.Succeeded));
        Assert.All(
            attempts.Where(attempt => !attempt.Succeeded),
            attempt => Assert.Equal(LoginStatus.UnknownOrSpentChallenge, attempt.Status));
    }

    [RequiresDockerFact]
    public async Task AFailedSignature_StillSpendsTheChallenge()
    {
        // Spending before verifying is deliberate: one attempt per nonce, rather than unlimited
        // attempts against a challenge that stays valid until it expires.
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var issued = await server.IssueAsync();
        var key = identity.Public.SigningKey.ToArray();

        var wrong = await server.AuthAsync(auth =>
            auth.LoginAsync(key, issued.Nonce, new byte[IdentityKeyPair.SignatureSize]));
        Assert.Equal(LoginStatus.InvalidSignature, wrong.Status);

        // The correct signature now arrives too late — its challenge is gone.
        var correct = LoginChallenge.Sign(identity, issued.ServerId, issued.Nonce);
        var retry = await server.AuthAsync(auth => auth.LoginAsync(key, issued.Nonce, correct));

        Assert.Equal(LoginStatus.UnknownOrSpentChallenge, retry.Status);
    }

    // --- Expiry --------------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task AnExpiredChallenge_IsRejected()
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var issued = await server.IssueAsync();
        await server.ExpireAsync(issued.Nonce, DateTimeOffset.UtcNow.AddMinutes(-1));

        var signature = LoginChallenge.Sign(identity, issued.ServerId, issued.Nonce);
        var result = await server.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), issued.Nonce, signature));

        Assert.Equal(LoginStatus.ExpiredChallenge, result.Status);
    }

    [RequiresDockerFact]
    public async Task AnExpiredChallenge_IsNotSpent()
    {
        // It was never usable, so it is not consumed — which is what lets a later presentation
        // still be told it expired rather than that it was replayed.
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var issued = await server.IssueAsync();
        await server.ExpireAsync(issued.Nonce, DateTimeOffset.UtcNow.AddMinutes(-1));

        var signature = LoginChallenge.Sign(identity, issued.ServerId, issued.Nonce);
        await server.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), issued.Nonce, signature));

        var stored = await server.FindNonceAsync(issued.Nonce);
        Assert.NotNull(stored);
        Assert.Null(stored.ConsumedAt);
    }

    // --- Unknown challenges and keys -----------------------------------------------------------

    [RequiresDockerFact]
    public async Task AChallengeThisServerNeverIssued_IsRejected()
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var invented = new byte[LoginChallenge.NonceSize];
        Array.Fill(invented, (byte)7);

        var signature = LoginChallenge.Sign(identity, server.ServerId, invented);
        var result = await server.AuthAsync(auth =>
            auth.LoginAsync(identity.Public.SigningKey.ToArray(), invented, signature));

        Assert.Equal(LoginStatus.UnknownOrSpentChallenge, result.Status);
    }

    [RequiresDockerTheory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(64)]
    public async Task AMalformedNonce_IsRejectedWithoutTouchingTheTable(int length)
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var result = await server.AuthAsync(auth => auth.LoginAsync(
            identity.Public.SigningKey.ToArray(),
            new byte[length],
            new byte[IdentityKeyPair.SignatureSize]));

        Assert.Equal(LoginStatus.UnknownOrSpentChallenge, result.Status);
        Assert.Equal(0, await server.CountNoncesAsync());
    }

    [RequiresDockerFact]
    public async Task ASignatureFromADifferentKey_IsRejected()
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var claimed = IdentityKeyPair.Generate();
        using var actualSigner = IdentityKeyPair.Generate();

        await server.RegisterAsync(claimed);
        await server.RegisterAsync(actualSigner, "Someone Else");

        var issued = await server.IssueAsync();

        // Correctly formed, correctly targeted, signed by the wrong identity.
        var signature = LoginChallenge.Sign(actualSigner, issued.ServerId, issued.Nonce);

        var result = await server.AuthAsync(auth =>
            auth.LoginAsync(claimed.Public.SigningKey.ToArray(), issued.Nonce, signature));

        Assert.Equal(LoginStatus.InvalidSignature, result.Status);
    }

    [RequiresDockerFact]
    public async Task AnUnregisteredKey_IsRejectedDespiteAValidSignature()
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var stranger = IdentityKeyPair.Generate();

        var issued = await server.IssueAsync();
        var signature = LoginChallenge.Sign(stranger, issued.ServerId, issued.Nonce);

        var result = await server.AuthAsync(auth =>
            auth.LoginAsync(stranger.Public.SigningKey.ToArray(), issued.Nonce, signature));

        // The signature is genuine; the key is simply not a member here. Registration comes first
        // (§5.2), and the refusal is distinct in the server's own log without being distinct to the
        // caller.
        Assert.Equal(LoginStatus.UnknownAccount, result.Status);
    }

    [RequiresDockerTheory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task AMalformedPublicKey_IsRejected(int length)
    {
        await using var server = await LoginHost.StartAsync(postgres);

        var issued = await server.IssueAsync();

        var result = await server.AuthAsync(auth => auth.LoginAsync(
            new byte[length],
            issued.Nonce,
            new byte[IdentityKeyPair.SignatureSize]));

        Assert.Equal(LoginStatus.InvalidSignature, result.Status);
    }

    // --- Housekeeping --------------------------------------------------------------------------

    [RequiresDockerFact]
    public async Task TheSweep_RemovesOnlyChallengesPastRetention()
    {
        await using var server = await LoginHost.StartAsync(postgres);

        var stale = await server.IssueAsync();
        var recentlyExpired = await server.IssueAsync();
        var live = await server.IssueAsync();

        // Retention defaults to 15 minutes past expiry.
        await server.ExpireAsync(stale.Nonce, DateTimeOffset.UtcNow.AddHours(-1));
        await server.ExpireAsync(recentlyExpired.Nonce, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(1, await server.AuthAsync(auth => auth.SweepExpiredChallengesAsync()));

        Assert.Null(await server.FindNonceAsync(stale.Nonce));

        // Kept deliberately: a replay arriving just after expiry should still meet a row and be
        // refused, rather than looking like a nonce that never existed.
        Assert.NotNull(await server.FindNonceAsync(recentlyExpired.Nonce));
        Assert.NotNull(await server.FindNonceAsync(live.Nonce));
    }

    [RequiresDockerFact]
    public async Task TheSweep_DoesNotMakeASpentChallengeUsableAgain()
    {
        await using var server = await LoginHost.StartAsync(postgres);
        using var identity = IdentityKeyPair.Generate();
        await server.RegisterAsync(identity);

        var issued = await server.IssueAsync();
        var signature = LoginChallenge.Sign(identity, issued.ServerId, issued.Nonce);
        var key = identity.Public.SigningKey.ToArray();

        Assert.True((await server.AuthAsync(auth =>
            auth.LoginAsync(key, issued.Nonce, signature))).Succeeded);

        // Sweeping the row away removes the evidence, but the challenge is still not usable: an
        // unknown nonce is refused for the same reason a spent one is.
        await server.ExpireAsync(issued.Nonce, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(1, await server.AuthAsync(auth => auth.SweepExpiredChallengesAsync()));

        var replay = await server.AuthAsync(auth => auth.LoginAsync(key, issued.Nonce, signature));

        Assert.Equal(LoginStatus.UnknownOrSpentChallenge, replay.Status);
    }
}
