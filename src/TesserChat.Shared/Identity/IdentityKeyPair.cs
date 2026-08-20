using System.Security.Cryptography;
using NSec.Cryptography;

namespace TesserChat.Shared.Identity;

/// <summary>
/// A complete TesserChat identity: an Ed25519 signing keypair and an X25519 encryption keypair,
/// generated and held together. See docs/ARCHITECTURE.md §4.1.
/// </summary>
/// <remarks>
/// <para>
/// The two keys stay distinct and are used with distinct algorithms, so a flaw or misuse in either
/// one does not implicate the other. They are not, however, independent secrets: an identity has a
/// single master seed, and the X25519 key is derived from it (see <see cref="FromSeed"/>). That
/// keeps backup and multi-device transfer down to one secret, which is what the user actually has
/// to move and safeguard.
/// </para>
/// <para>
/// The derivation uses HKDF, <b>not</b> the Ed25519→X25519 birational map. That conversion exists
/// in libsodium but is not exposed by NSec, and implementing curve arithmetic by hand in the
/// project's most security-sensitive code would be a poor trade for the 32 bytes it saves.
/// </para>
/// <para>
/// <b>This holds live private key material.</b> It owns unmanaged key handles and must be disposed.
/// Nothing here writes to disk: persisting an identity to the OS keystore is a separate concern
/// and is not implemented yet, so an identity currently lives only as long as the process does.
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

    /// <summary>
    /// Length in bytes of the master seed that an entire identity is reconstructed from.
    /// </summary>
    public const int SeedSize = 32;

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

    /// <summary>
    /// This identity's permanent account id on any server, derived from the signing key.
    /// </summary>
    public Guid AccountId => Public.AccountId;

    /// <summary>
    /// Generates a brand-new identity from a fresh random seed.
    /// </summary>
    public static IdentityKeyPair Generate()
    {
        Span<byte> seed = stackalloc byte[SeedSize];
        RandomNumberGenerator.Fill(seed);
        try
        {
            return FromSeed(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>
    /// Reconstructs a complete identity from its <see cref="SeedSize"/>-byte master seed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seed <i>is</i> the identity. The Ed25519 signing key is the seed directly; the X25519
    /// encryption key is derived from it with HKDF-SHA256 under a fixed domain-separated info
    /// string. Both are reproduced exactly, so backing up and restoring one 32-byte secret carries
    /// the whole identity — the user moves a single file with a single passphrase rather than
    /// tracking two keys that could drift apart or be restored by halves.
    /// </para>
    /// <para>
    /// This is a key-derivation step, not a curve conversion: HKDF simply produces 32 deterministic
    /// bytes, which X25519 accepts as a private key because it clamps the scalar internally. No
    /// Ed25519→X25519 birational mapping is involved and no hand-written curve arithmetic exists
    /// anywhere in this project.
    /// </para>
    /// <para>
    /// <b>The consequence is that the two keys are not independent secrets.</b> Anyone holding the
    /// seed can derive the encryption key. What survives is the separation that actually matters
    /// here: two distinct keys used with two distinct algorithms, so neither algorithm's misuse
    /// implicates the other. Since both keys always shared one keystore entry and one backup file,
    /// treating them as independent was never true in practice — this makes it explicit.
    /// </para>
    /// <para>
    /// <b><see cref="X25519DerivationInfo"/> is frozen wire format.</b> Changing it makes every
    /// restored identity derive a different encryption key, silently breaking decryption of all
    /// existing direct messages.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The seed is not <see cref="SeedSize"/> bytes.</exception>
    /// <exception cref="CryptographicException">The seed is not valid key material.</exception>
    public static IdentityKeyPair FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException(
                $"An identity seed must be {SeedSize} bytes, got {seed.Length}.",
                nameof(seed));
        }

        Span<byte> encryptionSeed = stackalloc byte[SeedSize];
        try
        {
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: seed,
                output: encryptionSeed,
                salt: null,
                info: X25519DerivationInfo);

            return FromPrivateKeys(seed, encryptionSeed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionSeed);
        }
    }

    /// <summary>
    /// Reconstructs an identity from previously exported private key seeds.
    /// </summary>
    /// <remarks>
    /// This is the path the OS-keystore read and the encrypted-backup import will use. It takes
    /// the private halves only — both public keys are recomputed from them, so a stored public key
    /// can never disagree with the private key it claims to match.
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
    /// Used for the login challenge-response: the client proves it holds the private key by
    /// signing a short-lived nonce the server issued. Callers must sign a payload that binds the
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
    /// encrypt direct messages with that peer.
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
    /// long-term keys with <b>no forward secrecy</b> — a deliberate v1 tradeoff, since a proper
    /// Double Ratchet has no mature .NET implementation. Compromise of one private key
    /// retroactively decrypts every DM that identity ever exchanged.
    /// </para>
    /// <para>
    /// Deriving this is not free and the inputs never change, so the result is intended to be
    /// cached per peer after the first computation.
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
    /// Derives the direct-message key for a peer identity.
    /// </summary>
    public Key DeriveSharedKey(PublicIdentity peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return DeriveSharedKey(peer.EncryptionKey);
    }

    /// <summary>
    /// Exports the master seed this identity can be reconstructed from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the encrypted backup and the OS keystore should store: a single
    /// <see cref="SeedSize"/>-byte secret that <see cref="FromSeed"/> expands back into the whole
    /// identity. Storing one secret rather than two removes any possibility of a backup that
    /// restores half an identity.
    /// </para>
    /// <para>
    /// <b>Handle with care.</b> These bytes are the identity — anyone holding them can sign as this
    /// user and decrypt every direct message it has ever received. Never log it, never send it to a
    /// server, never write it to disk unencrypted, and clear the array as soon as you are done with
    /// it (<see cref="CryptographicOperations.ZeroMemory"/>).
    /// </para>
    /// </remarks>
    public byte[] ExportSeed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The Ed25519 raw private key is the seed itself, so no separate copy has to be kept alive
        // in managed memory for the lifetime of the identity.
        return _signingKey.Export(KeyBlobFormat.RawPrivateKey);
    }

    /// <summary>
    /// Exports both raw private keys separately.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ExportSeed"/> for backup and keystore writes — one secret is simpler to
    /// move between devices and cannot be restored by halves. This overload exists for the case
    /// where the two keys are genuinely independent, which is true of any identity built through
    /// <see cref="FromPrivateKeys"/> rather than <see cref="FromSeed"/>. The same handling warnings
    /// as <see cref="ExportSeed"/> apply, twice over.
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
    /// Keys are created exportable so the encrypted-backup flow and the OS-keystore write can
    /// reach the private bytes. NSec fixes this policy at creation — it cannot be relaxed
    /// afterwards, so a non-exportable key could never be backed up.
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

    /// <summary>
    /// HKDF info string separating the X25519 encryption key from the seed it is derived from.
    /// </summary>
    /// <remarks>
    /// <b>Frozen wire format.</b> Every identity restored from a seed derives its encryption key
    /// through this string. Changing it silently changes that key, which breaks decryption of every
    /// direct message the identity has ever exchanged — with no error at restore time to warn that
    /// it happened.
    /// </remarks>
    private static ReadOnlySpan<byte> X25519DerivationInfo => "tesserchat:x25519-from-ed25519-seed:v1"u8;
}
