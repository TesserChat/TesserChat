using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using TesserChat.Shared.Identity;

namespace TesserChat.Shared.Tests.Identity;

/// <summary>
/// Covers generation, signing, and key agreement. Negative cases are the substance here: a signature
/// check that never fails is indistinguishable from one that always passes (§0.1).
/// </summary>
public sealed class IdentityKeyPairTests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("tesserchat-login-challenge");

    [Fact]
    public void Generate_ProducesTwoDistinctKeyPairs()
    {
        using var identity = IdentityKeyPair.Generate();

        Assert.Equal(IdentityKeyPair.PublicKeySize, identity.Public.SigningKey.Length);
        Assert.Equal(IdentityKeyPair.PublicKeySize, identity.Public.EncryptionKey.Length);

        // The signing and encryption keys must be independently generated, never one derived from
        // the other — that separation is the §4.1 key-hygiene requirement.
        Assert.False(identity.Public.SigningKey.SequenceEqual(identity.Public.EncryptionKey));
    }

    [Fact]
    public void Generate_ProducesADifferentIdentityEachTime()
    {
        using var first = IdentityKeyPair.Generate();
        using var second = IdentityKeyPair.Generate();

        Assert.False(first.Public.SigningKey.SequenceEqual(second.Public.SigningKey));
        Assert.False(first.Public.EncryptionKey.SequenceEqual(second.Public.EncryptionKey));
        Assert.NotEqual(first.AccountId, second.AccountId);
    }

    [Fact]
    public void Sign_ThenVerify_RoundTrips()
    {
        using var identity = IdentityKeyPair.Generate();

        var signature = identity.Sign(Message);

        Assert.Equal(IdentityKeyPair.SignatureSize, signature.Length);
        Assert.True(IdentityKeyPair.Verify(identity.Public, Message, signature));
    }

    [Fact]
    public void Verify_FailsWhenTheMessageIsTampered()
    {
        using var identity = IdentityKeyPair.Generate();
        var signature = identity.Sign(Message);

        var tampered = (byte[])Message.Clone();
        tampered[0] ^= 0xFF;

        Assert.False(IdentityKeyPair.Verify(identity.Public, tampered, signature));
    }

    [Fact]
    public void Verify_FailsWhenTheSignatureIsTampered()
    {
        using var identity = IdentityKeyPair.Generate();
        var signature = identity.Sign(Message);
        signature[^1] ^= 0xFF;

        Assert.False(IdentityKeyPair.Verify(identity.Public, Message, signature));
    }

    [Fact]
    public void Verify_FailsAgainstADifferentIdentity()
    {
        using var signer = IdentityKeyPair.Generate();
        using var impostor = IdentityKeyPair.Generate();

        var signature = signer.Sign(Message);

        // The core auth property (§4.7): a valid signature proves possession of one specific
        // private key, and does not verify under anyone else's public key.
        Assert.False(IdentityKeyPair.Verify(impostor.Public, Message, signature));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Verify_ReturnsFalseForAMalformedPublicKey(int keyLength)
    {
        using var identity = IdentityKeyPair.Generate();
        var signature = identity.Sign(Message);

        // Malformed input from the wire must be a clean false, never an exception that a caller
        // could turn into a denial of service by sending garbage.
        Assert.False(IdentityKeyPair.Verify(new byte[keyLength], Message, signature));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    public void Verify_ReturnsFalseForAMalformedSignature(int signatureLength)
    {
        using var identity = IdentityKeyPair.Generate();

        Assert.False(IdentityKeyPair.Verify(identity.Public, Message, new byte[signatureLength]));
    }

    [Fact]
    public void Verify_FailsForAnEmptySignatureOverAnEmptyMessage()
    {
        using var identity = IdentityKeyPair.Generate();

        Assert.False(IdentityKeyPair.Verify(identity.Public, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Sign_OverEmptyData_StillVerifies()
    {
        using var identity = IdentityKeyPair.Generate();

        var signature = identity.Sign(ReadOnlySpan<byte>.Empty);

        Assert.True(IdentityKeyPair.Verify(identity.Public, ReadOnlySpan<byte>.Empty, signature));
    }

    [Fact]
    public void DeriveSharedKey_BothSidesAgreeOnTheSameKey()
    {
        using var alice = IdentityKeyPair.Generate();
        using var bob = IdentityKeyPair.Generate();

        using var aliceKey = alice.DeriveSharedKey(bob.Public);
        using var bobKey = bob.DeriveSharedKey(alice.Public);

        // Proven by use rather than by comparing key bytes: what actually matters is that a message
        // Alice encrypts is one Bob can decrypt (§7.1).
        var aead = AeadAlgorithm.XChaCha20Poly1305;
        var nonce = new byte[aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var plaintext = Encoding.UTF8.GetBytes("dinner at eight");
        var ciphertext = aead.Encrypt(aliceKey, nonce, ReadOnlySpan<byte>.Empty, plaintext);
        var decrypted = aead.Decrypt(bobKey, nonce, ReadOnlySpan<byte>.Empty, ciphertext);

        Assert.NotNull(decrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DeriveSharedKey_IsStableAcrossRepeatedCalls()
    {
        using var alice = IdentityKeyPair.Generate();
        using var bob = IdentityKeyPair.Generate();

        var aead = AeadAlgorithm.XChaCha20Poly1305;
        var nonce = new byte[aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var plaintext = Encoding.UTF8.GetBytes("cache me");

        using var first = alice.DeriveSharedKey(bob.Public);
        var ciphertext = aead.Encrypt(first, nonce, ReadOnlySpan<byte>.Empty, plaintext);

        // §7.1 caches the derived secret per peer, which is only safe if re-deriving gives the
        // same key — otherwise a cached key would silently stop matching fresh ones.
        using var second = alice.DeriveSharedKey(bob.Public);
        var decrypted = aead.Decrypt(second, nonce, ReadOnlySpan<byte>.Empty, ciphertext);

        Assert.NotNull(decrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DeriveSharedKey_WithAnUnrelatedPeerProducesADifferentKey()
    {
        using var alice = IdentityKeyPair.Generate();
        using var bob = IdentityKeyPair.Generate();
        using var eve = IdentityKeyPair.Generate();

        var aead = AeadAlgorithm.XChaCha20Poly1305;
        var nonce = new byte[aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        using var aliceToBob = alice.DeriveSharedKey(bob.Public);
        var ciphertext = aead.Encrypt(aliceToBob, nonce, ReadOnlySpan<byte>.Empty, "for bob only"u8.ToArray());

        // Eve holds a valid keypair but is not the intended recipient: decryption must fail the
        // authentication tag rather than return garbage plaintext.
        using var eveToAlice = eve.DeriveSharedKey(alice.Public);
        Assert.Null(aead.Decrypt(eveToAlice, nonce, ReadOnlySpan<byte>.Empty, ciphertext));
    }

    [Fact]
    public void DeriveSharedKey_RejectsAnAllZeroPeerKey()
    {
        using var identity = IdentityKeyPair.Generate();

        // An all-zero X25519 key is low-order: agreement yields a shared secret the attacker
        // already knows. It must be rejected outright, never silently used.
        Assert.Throws<CryptographicException>(
            () => identity.DeriveSharedKey(new byte[IdentityKeyPair.PublicKeySize]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void DeriveSharedKey_RejectsAWrongLengthPeerKey(int keyLength)
    {
        using var identity = IdentityKeyPair.Generate();

        Assert.Throws<ArgumentException>(() => identity.DeriveSharedKey(new byte[keyLength]));
    }

    [Fact]
    public void FromPrivateKeys_ReconstructsTheSameIdentity()
    {
        using var original = IdentityKeyPair.Generate();
        var (signingPrivate, encryptionPrivate) = original.ExportPrivateKeys();

        using var restored = IdentityKeyPair.FromPrivateKeys(signingPrivate, encryptionPrivate);

        // This is the multi-device story (§4.4) and the backup-restore path (§4.5): the same private
        // key must yield the same public identity and therefore the same account on every server.
        Assert.Equal(original.AccountId, restored.AccountId);
        Assert.True(original.Public.SigningKey.SequenceEqual(restored.Public.SigningKey));
        Assert.True(original.Public.EncryptionKey.SequenceEqual(restored.Public.EncryptionKey));
    }

    [Fact]
    public void FromPrivateKeys_RestoredIdentityProducesVerifiableSignatures()
    {
        using var original = IdentityKeyPair.Generate();
        var (signingPrivate, encryptionPrivate) = original.ExportPrivateKeys();

        using var restored = IdentityKeyPair.FromPrivateKeys(signingPrivate, encryptionPrivate);
        var signature = restored.Sign(Message);

        // A restored identity must be able to log in (§4.7) against the original's public key.
        Assert.True(IdentityKeyPair.Verify(original.Public, Message, signature));
    }

    [Fact]
    public void FromPrivateKeys_RestoredIdentityDerivesTheSameDmKey()
    {
        using var alice = IdentityKeyPair.Generate();
        using var bob = IdentityKeyPair.Generate();
        var (signingPrivate, encryptionPrivate) = alice.ExportPrivateKeys();

        using var restoredAlice = IdentityKeyPair.FromPrivateKeys(signingPrivate, encryptionPrivate);

        var aead = AeadAlgorithm.XChaCha20Poly1305;
        var nonce = new byte[aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var plaintext = Encoding.UTF8.GetBytes("history follows the key");

        using var bobKey = bob.DeriveSharedKey(alice.Public);
        var ciphertext = aead.Encrypt(bobKey, nonce, ReadOnlySpan<byte>.Empty, plaintext);

        // Restoring a key on a second device must unlock existing DM history (§4.4, §7.3).
        using var restoredKey = restoredAlice.DeriveSharedKey(bob.Public);
        Assert.Equal(plaintext, aead.Decrypt(restoredKey, nonce, ReadOnlySpan<byte>.Empty, ciphertext));
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(31, 32)]
    [InlineData(32, 31)]
    [InlineData(32, 0)]
    public void FromPrivateKeys_RejectsWrongLengthSeeds(int signingLength, int encryptionLength)
    {
        Assert.Throws<ArgumentException>(
            () => IdentityKeyPair.FromPrivateKeys(new byte[signingLength], new byte[encryptionLength]));
    }

    [Fact]
    public void ExportPrivateKeys_ReturnsSeedsOfTheExpectedSize()
    {
        using var identity = IdentityKeyPair.Generate();

        var (signingPrivate, encryptionPrivate) = identity.ExportPrivateKeys();

        Assert.Equal(IdentityKeyPair.PrivateKeySize, signingPrivate.Length);
        Assert.Equal(IdentityKeyPair.PrivateKeySize, encryptionPrivate.Length);
        Assert.False(signingPrivate.SequenceEqual(encryptionPrivate));
    }

    [Fact]
    public void ExportPrivateKeys_DoesNotReturnThePublicKey()
    {
        using var identity = IdentityKeyPair.Generate();

        var (signingPrivate, _) = identity.ExportPrivateKeys();

        // Guards against an export-format mix-up silently handing out the wrong half of the keypair.
        Assert.False(signingPrivate.AsSpan().SequenceEqual(identity.Public.SigningKey));
    }

    [Fact]
    public void UsingADisposedIdentityThrows()
    {
        var identity = IdentityKeyPair.Generate();
        using var peer = IdentityKeyPair.Generate();
        identity.Dispose();

        Assert.Throws<ObjectDisposedException>(() => identity.Sign(Message));
        Assert.Throws<ObjectDisposedException>(() => identity.ExportPrivateKeys());
        Assert.Throws<ObjectDisposedException>(() => identity.DeriveSharedKey(peer.Public));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var identity = IdentityKeyPair.Generate();

        identity.Dispose();

        // Double-dispose must not throw — using-blocks and explicit cleanup routinely overlap.
        identity.Dispose();
    }

    [Fact]
    public void Verify_ThrowsWhenTheSignerIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => IdentityKeyPair.Verify((PublicIdentity)null!, Message, new byte[IdentityKeyPair.SignatureSize]));
    }
}
