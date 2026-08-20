using System.Buffers.Text;

namespace TesserChat.Shared.Identity;

/// <summary>
/// The public half of an identity: the two public keys plus the account id they derive.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape that travels — published to a server on join, exchanged so a direct-message
/// partner can discover an encryption key, and encoded into an "add friend" string. It contains no
/// secret material and is safe to persist, log, or hand to anyone.
/// </para>
/// <para>
/// Both keys are carried together deliberately. A peer needs the Ed25519 key to verify what the
/// identity signs and the X25519 key to encrypt to it; splitting them across two lookups invites
/// pairing the wrong two keys.
/// </para>
/// </remarks>
public sealed class PublicIdentity : IEquatable<PublicIdentity>
{
    private readonly byte[] _signingKey;
    private readonly byte[] _encryptionKey;

    /// <summary>
    /// Wraps raw public key bytes. Both arrays are copied, so later mutation by the caller cannot
    /// change an identity that has already been handed out.
    /// </summary>
    /// <exception cref="ArgumentException">Either key is the wrong length.</exception>
    public PublicIdentity(ReadOnlySpan<byte> signingKey, ReadOnlySpan<byte> encryptionKey)
    {
        if (signingKey.Length != IdentityKeyPair.PublicKeySize)
        {
            throw new ArgumentException(
                $"An Ed25519 public key must be {IdentityKeyPair.PublicKeySize} bytes, got {signingKey.Length}.",
                nameof(signingKey));
        }

        if (encryptionKey.Length != IdentityKeyPair.PublicKeySize)
        {
            throw new ArgumentException(
                $"An X25519 public key must be {IdentityKeyPair.PublicKeySize} bytes, got {encryptionKey.Length}.",
                nameof(encryptionKey));
        }

        _signingKey = signingKey.ToArray();
        _encryptionKey = encryptionKey.ToArray();
        AccountId = Identity.AccountId.FromPublicKey(_signingKey);
    }

    /// <summary>Raw Ed25519 public key — verifies this identity's signatures.</summary>
    public ReadOnlySpan<byte> SigningKey => _signingKey;

    /// <summary>Raw X25519 public key — the direct-message key-exchange half.</summary>
    public ReadOnlySpan<byte> EncryptionKey => _encryptionKey;

    /// <summary>
    /// This identity's permanent account id, derived from <see cref="SigningKey"/>.
    /// </summary>
    public Guid AccountId { get; }

    /// <summary>
    /// Encodes both keys into a single self-contained token — the form used for "add friend"
    /// strings and for contact export files.
    /// </summary>
    /// <remarks>
    /// Base64url, so the token survives being pasted into a URL, a chat message, or a filename
    /// without escaping. The two keys are fixed-width and concatenated, so no length prefix or
    /// delimiter is needed.
    /// </remarks>
    public string ToShareableString()
    {
        Span<byte> combined = stackalloc byte[IdentityKeyPair.PublicKeySize * 2];
        _signingKey.CopyTo(combined);
        _encryptionKey.CopyTo(combined[IdentityKeyPair.PublicKeySize..]);
        return Base64Url.EncodeToString(combined);
    }

    /// <summary>
    /// Parses a token produced by <see cref="ToShareableString"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> rather than throwing on any malformed input — this parses
    /// strings pasted by hand, so rejection is an expected outcome, not an exceptional one.
    /// </remarks>
    public static bool TryParse(string? token, out PublicIdentity? identity)
    {
        identity = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(token.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != IdentityKeyPair.PublicKeySize * 2)
        {
            return false;
        }

        identity = new PublicIdentity(
            bytes.AsSpan(0, IdentityKeyPair.PublicKeySize),
            bytes.AsSpan(IdentityKeyPair.PublicKeySize));
        return true;
    }

    /// <summary>
    /// A short, human-comparable rendering of the signing key, for the fingerprint the client
    /// shows the user and for out-of-band verification against a contact.
    /// </summary>
    /// <remarks>
    /// Groups of four hex characters, since people compare these visually and unbroken 64-character
    /// strings are read badly. This is a display aid — never accept a fingerprint where a full key
    /// is required.
    /// </remarks>
    public string ToFingerprint()
    {
        var hex = Convert.ToHexStringLower(_signingKey);
        return string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>
    /// Identity equality is equality of the signing key, compared in constant time.
    /// </summary>
    /// <remarks>
    /// Constant-time because this comparison decides "is this the contact I think it is" — a
    /// timing-variable compare would leak how much of a key an attacker had guessed correctly.
    /// </remarks>
    public bool Equals(PublicIdentity? other)
        => other is not null
           && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(_signingKey, other._signingKey)
           && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(_encryptionKey, other._encryptionKey);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PublicIdentity);

    /// <inheritdoc />
    public override int GetHashCode() => AccountId.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => $"PublicIdentity({Identity.AccountId.ToCanonicalString(AccountId)})";
}
