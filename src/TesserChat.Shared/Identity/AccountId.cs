using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace TesserChat.Shared.Identity;

/// <summary>
/// An account's permanent identifier on a server, derived from its Ed25519 public key.
/// </summary>
/// <remarks>
/// <para>
/// Servers key everything internal off this value — permissions, message authorship, the audit
/// trail — rather than off the display name, which is cosmetic and freely changeable.
/// See docs/ARCHITECTURE.md §5.1.
/// </para>
/// <para>
/// Derivation is a domain-separated SHA-256 over the raw public key, truncated to 16 bytes and
/// shaped into a RFC 4122 version 8 (custom) UUID. Truncating a hash to 128 bits is sound here:
/// this identifies an account, it is not a security boundary. Possession of the private key is
/// what authenticates a login; a collision would let two identities share a row, not let one
/// impersonate the other's signatures.
/// </para>
/// <para>
/// The derivation is deterministic and must stay stable forever — the same public key presented to
/// the same server next year has to resolve to the same account. Changing the domain separator or
/// the hash would silently orphan every existing account, so treat this as frozen wire format.
/// </para>
/// </remarks>
public static class AccountId
{
    /// <summary>
    /// Domain separator, mixed in so this hash can never collide with some other use of SHA-256
    /// over the same key bytes elsewhere in the protocol.
    /// </summary>
    private const string DomainSeparator = "tesserchat:account-id:v1";

    /// <summary>
    /// Derives the permanent account UUID for an Ed25519 public key.
    /// </summary>
    /// <param name="ed25519PublicKey">Raw 32-byte Ed25519 public key.</param>
    /// <exception cref="ArgumentException">
    /// The key is not <see cref="IdentityKeyPair.PublicKeySize"/> bytes long.
    /// </exception>
    public static Guid FromPublicKey(ReadOnlySpan<byte> ed25519PublicKey)
    {
        if (ed25519PublicKey.Length != IdentityKeyPair.PublicKeySize)
        {
            throw new ArgumentException(
                $"An Ed25519 public key must be {IdentityKeyPair.PublicKeySize} bytes, got {ed25519PublicKey.Length}.",
                nameof(ed25519PublicKey));
        }

        Span<byte> separator = stackalloc byte[DomainSeparator.Length];
        System.Text.Encoding.ASCII.GetBytes(DomainSeparator, separator);

        Span<byte> input = stackalloc byte[separator.Length + IdentityKeyPair.PublicKeySize];
        separator.CopyTo(input);
        ed25519PublicKey.CopyTo(input[separator.Length..]);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);

        return ToVersion8Uuid(digest[..16]);
    }

    /// <summary>
    /// Stamps the RFC 4122 version and variant bits onto 16 bytes of hash output.
    /// </summary>
    /// <remarks>
    /// Version 8 is the "custom / application-defined" version, which is exactly what this is: a
    /// UUID whose bits come from a hash of our own construction rather than from time or randomness.
    /// This costs 6 bits of the 128, leaving 122 bits of entropy.
    /// </remarks>
    private static Guid ToVersion8Uuid(Span<byte> bytes)
    {
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8, high nibble of byte 6
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant, top two bits of byte 8

        // Big-endian so the UUID's textual form tracks the hash bytes in order, independent of
        // the host's endianness. Guid's default constructor is little-endian for the first three
        // fields, which would make the same key render differently on a big-endian machine.
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// Renders an account id in the canonical lowercase, hyphenated form used on the wire.
    /// </summary>
    public static string ToCanonicalString(Guid accountId) => accountId.ToString("D");

    /// <summary>
    /// Parses an account id previously produced by <see cref="ToCanonicalString"/>.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Guid accountId)
        => Guid.TryParseExact(text, "D", out accountId);
}
