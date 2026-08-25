using System.Text;
using TesserChat.Shared.Auth;
using TesserChat.Shared.Identity;

namespace TesserChat.Shared.Tests.Auth;

/// <summary>
/// Covers the signed login payload (§4.7).
/// </summary>
/// <remarks>
/// This is frozen wire format, so the tests pin the layout byte for byte rather than only checking
/// that signing and verifying agree with each other. A round-trip test alone passes happily while
/// both sides drift together — which is exactly the failure that would break every existing client
/// against every existing server.
/// </remarks>
public sealed class LoginChallengeTests
{
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("tesserchat:login:v1");

    // --- The layout, pinned --------------------------------------------------------------------

    [Fact]
    public void ThePayload_IsContextThenServerIdThenNonce()
    {
        var serverId = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10");

        var nonce = new byte[LoginChallenge.NonceSize];
        for (var i = 0; i < nonce.Length; i++)
        {
            nonce[i] = (byte)(0xA0 + i);
        }

        var payload = LoginChallenge.BuildPayload(serverId, nonce);

        Assert.Equal(Context.Length + 16 + LoginChallenge.NonceSize, payload.Length);
        Assert.Equal(Context, payload[..Context.Length]);

        // Big-endian, so the bytes read in the same order as the UUID's canonical text form.
        Assert.Equal(
            serverId.ToByteArray(bigEndian: true),
            payload[Context.Length..(Context.Length + 16)]);

        Assert.Equal(nonce, payload[(Context.Length + 16)..]);
    }

    [Fact]
    public void PayloadSize_MatchesWhatIsActuallyWritten()
    {
        // Pins the hand-written ContextLength constant against the context string itself: editing
        // one without the other shifts every field after it, and would otherwise be caught only by
        // a client failing to log in against a server on a different build.
        var payload = LoginChallenge.BuildPayload(Guid.NewGuid(), new byte[LoginChallenge.NonceSize]);

        Assert.Equal(LoginChallenge.PayloadSize, payload.Length);
        Assert.Equal(LoginChallenge.PayloadSize, Context.Length + 16 + LoginChallenge.NonceSize);
    }

    [Fact]
    public void ThePayload_IsDeterministic()
    {
        var serverId = Guid.NewGuid();
        var nonce = new byte[LoginChallenge.NonceSize];
        Random.Shared.NextBytes(nonce);

        Assert.Equal(
            LoginChallenge.BuildPayload(serverId, nonce),
            LoginChallenge.BuildPayload(serverId, nonce));
    }

    [Fact]
    public void ADifferentServerId_ChangesThePayload()
    {
        var nonce = new byte[LoginChallenge.NonceSize];

        Assert.NotEqual(
            LoginChallenge.BuildPayload(Guid.NewGuid(), nonce),
            LoginChallenge.BuildPayload(Guid.NewGuid(), nonce));
    }

    // --- Signing and verifying -----------------------------------------------------------------

    [Fact]
    public void ASignature_VerifiesForTheSameServerAndNonce()
    {
        using var identity = IdentityKeyPair.Generate();
        var serverId = Guid.NewGuid();
        var nonce = RandomNonce();

        var signature = LoginChallenge.Sign(identity, serverId, nonce);

        Assert.True(LoginChallenge.Verify(identity.Public.SigningKey, serverId, nonce, signature));
    }

    [Fact]
    public void ASignature_DoesNotVerifyForADifferentServer()
    {
        // The replay property, at the level it is actually provided: the bytes signed differ, so
        // the signature cannot verify no matter who presents it.
        using var identity = IdentityKeyPair.Generate();
        var nonce = RandomNonce();

        var signature = LoginChallenge.Sign(identity, Guid.NewGuid(), nonce);

        Assert.False(LoginChallenge.Verify(identity.Public.SigningKey, Guid.NewGuid(), nonce, signature));
    }

    [Fact]
    public void ASignature_DoesNotVerifyForADifferentNonce()
    {
        using var identity = IdentityKeyPair.Generate();
        var serverId = Guid.NewGuid();

        var signature = LoginChallenge.Sign(identity, serverId, RandomNonce());

        Assert.False(LoginChallenge.Verify(identity.Public.SigningKey, serverId, RandomNonce(), signature));
    }

    [Fact]
    public void ASignature_DoesNotVerifyForADifferentKey()
    {
        using var signer = IdentityKeyPair.Generate();
        using var other = IdentityKeyPair.Generate();
        var serverId = Guid.NewGuid();
        var nonce = RandomNonce();

        var signature = LoginChallenge.Sign(signer, serverId, nonce);

        Assert.False(LoginChallenge.Verify(other.Public.SigningKey, serverId, nonce, signature));
    }

    [Fact]
    public void ATamperedSignature_DoesNotVerify()
    {
        using var identity = IdentityKeyPair.Generate();
        var serverId = Guid.NewGuid();
        var nonce = RandomNonce();

        var signature = LoginChallenge.Sign(identity, serverId, nonce);
        signature[0] ^= 0xFF;

        Assert.False(LoginChallenge.Verify(identity.Public.SigningKey, serverId, nonce, signature));
    }

    [Fact]
    public void TheDomainSeparator_KeepsALoginSignatureDistinctFromABareNonceSignature()
    {
        // Without the context prefix, a signature over the raw nonce would be a login signature.
        // Anything that ever gets an identity to sign attacker-chosen bytes would then be a login
        // oracle, so this is the property the separator exists for.
        using var identity = IdentityKeyPair.Generate();
        var serverId = Guid.NewGuid();
        var nonce = RandomNonce();

        var overRawNonce = identity.Sign(nonce);

        Assert.False(LoginChallenge.Verify(identity.Public.SigningKey, serverId, nonce, overRawNonce));
    }

    // --- Malformed input -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void AWrongLengthNonce_IsRefusedRatherThanSigned(int length)
    {
        using var identity = IdentityKeyPair.Generate();

        Assert.Throws<ArgumentException>(() =>
            LoginChallenge.Sign(identity, Guid.NewGuid(), new byte[length]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void AWrongLengthNonce_FailsVerificationWithoutThrowing(int length)
    {
        // Verification takes wire input, so rejection is an expected outcome rather than an
        // exceptional one — the server must not be made to throw by a malformed request.
        using var identity = IdentityKeyPair.Generate();

        Assert.False(LoginChallenge.Verify(
            identity.Public.SigningKey,
            Guid.NewGuid(),
            new byte[length],
            new byte[IdentityKeyPair.SignatureSize]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void AWrongLengthPublicKey_FailsVerificationWithoutThrowing(int length)
    {
        Assert.False(LoginChallenge.Verify(
            new byte[length],
            Guid.NewGuid(),
            RandomNonce(),
            new byte[IdentityKeyPair.SignatureSize]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    public void AWrongLengthSignature_FailsVerificationWithoutThrowing(int length)
    {
        using var identity = IdentityKeyPair.Generate();

        Assert.False(LoginChallenge.Verify(
            identity.Public.SigningKey,
            Guid.NewGuid(),
            RandomNonce(),
            new byte[length]));
    }

    [Fact]
    public void ATooSmallDestination_IsRefused()
    {
        var nonce = RandomNonce();

        Assert.Throws<ArgumentException>(() =>
        {
            var tooSmall = new byte[LoginChallenge.PayloadSize - 1];
            LoginChallenge.WritePayload(Guid.NewGuid(), nonce, tooSmall);
        });
    }

    [Fact]
    public void SigningWithoutAnIdentity_IsRefused()
        => Assert.Throws<ArgumentNullException>(() =>
            LoginChallenge.Sign(null!, Guid.NewGuid(), RandomNonce()));

    private static byte[] RandomNonce()
    {
        var nonce = new byte[LoginChallenge.NonceSize];
        Random.Shared.NextBytes(nonce);
        return nonce;
    }
}
