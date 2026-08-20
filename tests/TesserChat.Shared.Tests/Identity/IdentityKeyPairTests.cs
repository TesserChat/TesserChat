using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using TesserChat.Shared.Identity;

namespace TesserChat.Shared.Tests.Identity;

/// <summary>
/// Covers generation, signing, and key agreement. Negative cases are the substance here: a signature
/// check that never fails is indistinguishable from one that always passes.
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
        // the other: reusing one key across two algorithms means a flaw in either implicates both.
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

        // The core auth property: a valid signature proves possession of one specific private
        // key, and does not verify under anyone else's public key.
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
        // Alice encrypts is one Bob can decrypt.
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

        // Clients cache the derived secret per peer, which is only safe if re-deriving gives the
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
    public void FromSeed_ReconstructsTheSameIdentity()
    {
        using var original = IdentityKeyPair.Generate();
        var seed = original.ExportSeed();

        using var restored = IdentityKeyPair.FromSeed(seed);

        // The multi-device story and the backup-restore path, both from one secret: the same seed
        // must yield the same public identity, and therefore the same account on every server.
        Assert.Equal(original.AccountId, restored.AccountId);
        Assert.True(original.Public.SigningKey.SequenceEqual(restored.Public.SigningKey));
        Assert.True(original.Public.EncryptionKey.SequenceEqual(restored.Public.EncryptionKey));
    }

    [Fact]
    public void FromPrivateKeys_ReconstructsTheSameIdentity()
    {
        using var original = IdentityKeyPair.Generate();
        var (signingPrivate, encryptionPrivate) = original.ExportPrivateKeys();

        using var restored = IdentityKeyPair.FromPrivateKeys(signingPrivate, encryptionPrivate);

        Assert.Equal(original.AccountId, restored.AccountId);
        Assert.True(original.Public.SigningKey.SequenceEqual(restored.Public.SigningKey));
        Assert.True(original.Public.EncryptionKey.SequenceEqual(restored.Public.EncryptionKey));
    }

    [Fact]
    public void FromSeed_IsDeterministicAcrossDevices()
    {
        var seed = RandomNumberGenerator.GetBytes(IdentityKeyPair.SeedSize);

        using var deviceA = IdentityKeyPair.FromSeed(seed);
        using var deviceB = IdentityKeyPair.FromSeed(seed);

        // Importing the same backup on a second device must produce a byte-identical identity —
        // both keys, not just the signing one.
        Assert.Equal(deviceA.AccountId, deviceB.AccountId);
        Assert.True(deviceA.Public.SigningKey.SequenceEqual(deviceB.Public.SigningKey));
        Assert.True(deviceA.Public.EncryptionKey.SequenceEqual(deviceB.Public.EncryptionKey));
    }

    [Fact]
    public void FromSeed_DerivesTwoDistinctKeys()
    {
        var seed = RandomNumberGenerator.GetBytes(IdentityKeyPair.SeedSize);

        using var identity = IdentityKeyPair.FromSeed(seed);

        // Derived from one seed, but they must not collapse into the same key: they are used with
        // different algorithms and must stay separable.
        Assert.False(identity.Public.SigningKey.SequenceEqual(identity.Public.EncryptionKey));
    }

    [Fact]
    public void FromSeed_DerivesTheEncryptionKeyRatherThanReusingTheSeed()
    {
        var seed = RandomNumberGenerator.GetBytes(IdentityKeyPair.SeedSize);

        using var identity = IdentityKeyPair.FromSeed(seed);
        var (_, encryptionPrivate) = identity.ExportPrivateKeys();

        // Guards the HKDF step: handing the raw seed to X25519 unchanged would make the encryption
        // private key equal the signing private key, defeating the separation entirely.
        Assert.False(encryptionPrivate.AsSpan().SequenceEqual(seed));
    }

    [Fact]
    public void FromSeed_DifferentSeedsProduceDifferentIdentities()
    {
        using var first = IdentityKeyPair.FromSeed(RandomNumberGenerator.GetBytes(IdentityKeyPair.SeedSize));
        using var second = IdentityKeyPair.FromSeed(RandomNumberGenerator.GetBytes(IdentityKeyPair.SeedSize));

        Assert.NotEqual(first.AccountId, second.AccountId);
        Assert.False(first.Public.EncryptionKey.SequenceEqual(second.Public.EncryptionKey));
    }

    [Fact]
    public void FromSeed_ProducesAKnownEncryptionKeyForAKnownSeed()
    {
        // Pins the seed -> X25519 derivation, which is frozen wire format. If the HKDF info string,
        // hash, or input ever changes, every restored identity silently derives a different
        // encryption key and can no longer decrypt existing direct messages — with no error at
        // restore time to signal it happened.
        //
        // The expected value was computed independently of this codebase and of NSec, so it pins
        // the specified construction rather than echoing the implementation:
        //   priv = HKDF-SHA256(ikm: 32 zero bytes, salt: none,
        //                      info: "tesserchat:x25519-from-ed25519-seed:v1")
        //        = 837d9c561dc7e8612e2fb59b83504c5aa899f2906e7b6e4fc97a754503eaa676
        //   pub  = X25519(clamp(priv), 9)   per RFC 7748
        var seed = new byte[IdentityKeyPair.SeedSize];

        using var identity = IdentityKeyPair.FromSeed(seed);

        Assert.Equal(
            "ec61026e0cadaa34c89766ce5715903a6e3b98a4fb8ed7fb09a894ef6e0fc328",
            Convert.ToHexStringLower(identity.Public.EncryptionKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void FromSeed_RejectsAWrongLengthSeed(int seedLength)
    {
        Assert.Throws<ArgumentException>(() => IdentityKeyPair.FromSeed(new byte[seedLength]));
    }

    [Fact]
    public void ExportSeed_RoundTripsThroughFromSeed()
    {
        using var original = IdentityKeyPair.Generate();

        var seed = original.ExportSeed();
        using var restored = IdentityKeyPair.FromSeed(seed);

        Assert.Equal(IdentityKeyPair.SeedSize, seed.Length);
        Assert.Equal(original.AccountId, restored.AccountId);
    }

    [Fact]
    public void ExportSeed_RestoredIdentityDecryptsExistingDirectMessages()
    {
        using var alice = IdentityKeyPair.Generate();
        using var bob = IdentityKeyPair.Generate();
        var seed = alice.ExportSeed();

        var aead = AeadAlgorithm.XChaCha20Poly1305;
        var nonce = new byte[aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var plaintext = Encoding.UTF8.GetBytes("sent before the device was replaced");

        using var bobKey = bob.DeriveSharedKey(alice.Public);
        var ciphertext = aead.Encrypt(bobKey, nonce, ReadOnlySpan<byte>.Empty, plaintext);

        // The whole point of the single-seed design: restoring one secret on a new device must
        // unlock history, which only works if the derived encryption key is reproduced exactly.
        using var restoredAlice = IdentityKeyPair.FromSeed(seed);
        using var restoredKey = restoredAlice.DeriveSharedKey(bob.Public);

        Assert.Equal(plaintext, aead.Decrypt(restoredKey, nonce, ReadOnlySpan<byte>.Empty, ciphertext));
    }

    [Fact]
    public void FromSeed_RestoredIdentityProducesVerifiableSignatures()
    {
        using var original = IdentityKeyPair.Generate();
        var seed = original.ExportSeed();

        using var restored = IdentityKeyPair.FromSeed(seed);
        var signature = restored.Sign(Message);

        // A restored identity must be able to log in against the original's public key.
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

        // Restoring a key on a second device must unlock existing direct-message history.
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
    public void ExportPrivateKeys_ReturnsKeysOfTheExpectedSize()
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
        Assert.Throws<ObjectDisposedException>(() => identity.ExportSeed());
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
