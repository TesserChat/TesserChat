using System.Security.Cryptography;
using NSec.Cryptography;

namespace TesserChat.Shared.Identity;

/// <summary>
/// A complete TesserChat identity: an Ed25519 signing keypair and an X25519 encryption keypair,
/// generated and held together (§4.1).
/// </summary>
/// <remarks>
/// <para>
/// The two keypairs are independent. An Ed25519 key can be mathematically converted to X25519, and
/// this type deliberately does not do that — reusing one key across two algorithms means a flaw or
/// a misuse in either one implicates the other. Generating two costs a few microseconds once.
/// </para>
/// <para>
/// <b>This holds live private key material.</b> It owns unmanaged key handles and must be disposed.
/// Nothing here writes to disk: persisting an identity to the OS keystore is a separate concern
/// (§4.2 step 2) and is not implemented yet, so an identity currently lives only as long as the
/// process does.
/// </para>
/// </remarks>
public sealed class IdentityKeyPair : IDisposable
{
    /// <summary>Length in bytes of an Ed25519 or X25519 public key — both are 32.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Length in bytes of an Ed25519 or X25519 private key seed — both are 32.</summary>
    public const int PrivateKeySize = 32;

    /// <summary>Length in bytes of an Ed25519 signature.</summary>
    public const int SignatureSize = 64;

    private static readonly SignatureAlgorithm SigningAlgorithm = SignatureAlgorithm.Ed25519;
    private static readonly KeyAgreementAlgorithm AgreementAlgorithm = KeyAgreementAlgorithm.X25519;

    private readonly Key _signingKey;
    private readonly Key _encryptionKey;
    private bool _disposed;

    private IdentityKeyPair(Key signingKey, Key encryptionKey)
    {
        _signingKey = signingKey;
        _encryptionKey = encryptionKey;
        Public = new PublicIdentity(
            signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    /// <summary>The public half of this identity — safe to publish, share, and persist.</summary>
    public PublicIdentity Public { get; }

    /// <summary>This identity's permanent account id (§5.1).</summary>
    public Guid AccountId => Public.AccountId;

    /// <summary>
    /// Generates a brand-new identity: two fresh keypairs from the platform CSPRNG (§4.2 step 1).
    /// </summary>
    public static IdentityKeyPair Generate()
    {
        Key? signing = null;
        Key? encryption = null;
        try
        {
            signing = Key.Create(SigningAlgorithm, CreationParameters);
            encryption = Key.Create(AgreementAlgorithm, CreationParameters);
            var identity = new IdentityKeyPair(signing, encryption);

            // Ownership has transferred to the returned instance; don't let the catch dispose them.
            signing = null;
            encryption = null;
            return identity;
        }
        catch
        {
            signing?.Dispose();
            encryption?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reconstructs an identity from previously exported private key seeds.
    /// </summary>
    /// <remarks>
    /// This is the path the OS-keystore read (§4.2) and the encrypted-backup import (§4.5) will
    /// use. It takes the private halves only — both public keys are recomputed from them, so a
    /// stored public key can never disagree with the private key it claims to match.
    /// </remarks>
    /// <exception cref="ArgumentException">Either seed is the wrong length.</exception>
    /// <exception cref="CryptographicException">Either seed is not valid key material.</exception>
    public static IdentityKeyPair FromPrivateKeys(
        ReadOnlySpan<byte> signingPrivateKey,
        ReadOnlySpan<byte> encryptionPrivateKey)
    {
        if (signingPrivateKey.Length != PrivateKeySize)
        {
            throw new ArgumentException(
                $"An Ed25519 private key must be {PrivateKeySize} bytes, got {signingPrivateKey.Length}.",
                nameof(signingPrivateKey));
        }

        if (encryptionPrivateKey.Length != PrivateKeySize)
        {
            throw new ArgumentException(
                $"An X25519 private key must be {PrivateKeySize} bytes, got {encryptionPrivateKey.Length}.",
                nameof(encryptionPrivateKey));
        }

        Key? signing = null;
        Key? encryption = null;
        try
        {
            signing = Key.Import(SigningAlgorithm, signingPrivateKey, KeyBlobFormat.RawPrivateKey, CreationParameters);
            encryption = Key.Import(AgreementAlgorithm, encryptionPrivateKey, KeyBlobFormat.RawPrivateKey, CreationParameters);
            var identity = new IdentityKeyPair(signing, encryption);
            signing = null;
            encryption = null;
            return identity;
        }
        catch (FormatException ex)
        {
            signing?.Dispose();
            encryption?.Dispose();
            throw new CryptographicException("The supplied private key material is malformed.", ex);
        }
        catch
        {
            signing?.Dispose();
            encryption?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Signs <paramref name="data"/> with this identity's Ed25519 key.
    /// </summary>
    /// <remarks>
    /// Used for the login challenge-response (§4.7). Callers must sign a payload that binds the
    /// target server, so a captured signature cannot be replayed against a different one.
    /// </remarks>
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SigningAlgorithm.Sign(_signingKey, data);
    }

    /// <summary>
    /// Verifies a signature against a raw public key. Returns <see langword="false"/> for a bad
    /// signature, a wrong signer, or malformed input — it never throws on bad input.
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> signingPublicKey,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature)
    {
        if (signingPublicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        PublicKey imported;
        try
        {
            imported = PublicKey.Import(SigningAlgorithm, signingPublicKey, KeyBlobFormat.RawPublicKey);
        }
        catch (FormatException)
        {
            return false;
        }

        return SigningAlgorithm.Verify(imported, data, signature);
    }

    /// <summary>
    /// Verifies a signature made by <paramref name="signer"/>.
    /// </summary>
    public static bool Verify(PublicIdentity signer, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(signer);
        return Verify(signer.SigningKey, data, signature);
    }

    /// <summary>
    /// Performs X25519 ECDH against a peer's encryption key and derives the symmetric key used to
    /// encrypt DMs with that peer (§7.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The raw ECDH output is never used as a key directly — it is run through HKDF with a
    /// domain-separated info string, which is what turns a curve point into uniformly distributed
    /// key material.
    /// </para>
    /// <para>
    /// Both sides derive the same key: ECDH is symmetric and the HKDF inputs here are fixed. That
    /// symmetry is what lets two clients talk without a handshake. It also means these are static
    /// long-term keys with <b>no forward secrecy</b> — a deliberate v1 tradeoff (§7.1). Compromise
    /// of one private key retroactively decrypts every DM that identity ever exchanged.
    /// </para>
    /// <para>
    /// The result is safe to cache per peer, which §7.1 calls for.
    /// </para>
    /// </remarks>
    /// <returns>A key suitable for XChaCha20-Poly1305. The caller owns and must dispose it.</returns>
    /// <exception cref="ArgumentException">The peer key is the wrong length.</exception>
    /// <exception cref="CryptographicException">The peer key is not a usable X25519 point.</exception>
    public Key DeriveSharedKey(ReadOnlySpan<byte> peerEncryptionPublicKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (peerEncryptionPublicKey.Length != PublicKeySize)
        {
            throw new ArgumentException(
                $"An X25519 public key must be {PublicKeySize} bytes, got {peerEncryptionPublicKey.Length}.",
                nameof(peerEncryptionPublicKey));
        }

        PublicKey peer;
        try
        {
            peer = PublicKey.Import(AgreementAlgorithm, peerEncryptionPublicKey, KeyBlobFormat.RawPublicKey);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The peer's X25519 public key is malformed.", ex);
        }

        // Agree returns null for a low-order / all-zero peer key, which would otherwise yield a
        // shared secret an attacker already knows. Treat it as a hard failure, never a usable key.
        using var sharedSecret = AgreementAlgorithm.Agree(_encryptionKey, peer)
            ?? throw new CryptographicException(
                "X25519 key agreement failed — the peer's public key is not a usable curve point.");

        return KeyDerivationAlgorithm.HkdfSha256.DeriveKey(
            sharedSecret,
            salt: ReadOnlySpan<byte>.Empty,
            info: DmKeyDerivationInfo,
            algorithm: AeadAlgorithm.XChaCha20Poly1305);
    }

    /// <summary>
    /// Derives the DM key for a peer identity (§7.1).
    /// </summary>
    public Key DeriveSharedKey(PublicIdentity peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return DeriveSharedKey(peer.EncryptionKey);
    }

    /// <summary>
    /// Exports the raw private key seeds.
    /// </summary>
    /// <remarks>
    /// <b>Handle with care.</b> These bytes are the identity — anyone holding them can sign as this
    /// user and decrypt every DM it has ever received. This exists for the OS-keystore write (§4.2)
    /// and the encrypted backup (§4.5); it must never be logged, sent to a server, or written to
    /// disk unencrypted. Callers should clear the arrays when finished.
    /// </remarks>
    public (byte[] SigningPrivateKey, byte[] EncryptionPrivateKey) ExportPrivateKeys()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (
            _signingKey.Export(KeyBlobFormat.RawPrivateKey),
            _encryptionKey.Export(KeyBlobFormat.RawPrivateKey));
    }

    /// <summary>
    /// Releases the underlying key handles, wiping the private key material NSec holds.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _signingKey.Dispose();
        _encryptionKey.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Keys are created exportable so the encrypted-backup flow (§4.5) and the OS-keystore write
    /// (§4.2) can reach the private bytes. NSec fixes this policy at creation — it cannot be
    /// relaxed afterwards, so a non-exportable key could never be backed up.
    /// </summary>
    private static KeyCreationParameters CreationParameters => new()
    {
        ExportPolicy = KeyExportPolicies.AllowPlaintextExport
    };

    /// <summary>
    /// HKDF info string. Domain-separates this key from any other key later derived from the same
    /// ECDH secret; changing it breaks decryption of every existing DM, so treat it as wire format.
    /// </summary>
    private static ReadOnlySpan<byte> DmKeyDerivationInfo => "tesserchat:dm-key:v1"u8;
}
