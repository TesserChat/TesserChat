using System.Security.Cryptography;
using System.Text;

namespace TesserChat.Server.Accounts;

/// <summary>
/// Hashes and verifies the shared joining password of a password-gated server (§5.2 mode 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The password is never stored.</b> Configuration holds a salted PBKDF2 hash, so an operator who
/// reads their own database or a leaked backup does not thereby learn a secret they can hand to
/// someone else — and, since people reuse passwords, does not learn one that might open something
/// unrelated.
/// </para>
/// <para>
/// <b>PBKDF2 rather than Argon2, deliberately, and only here.</b> §4.5 specifies Argon2id for the
/// encrypted key backup and that still stands: a backup file is stolen outright and attacked
/// offline forever, so memory-hardness is what stands between an attacker and an identity. A
/// joining password is a different problem — it is server-side, it gates one action, an operator
/// can rotate it the moment it leaks, and it protects joining rather than an identity. PBKDF2 from
/// the BCL is adequate for that and avoids a dependency for a subsystem that does not need one. If
/// Argon2 arrives for §4.5, revisiting this is cheap: only the stored format changes, and only for
/// servers using this mode.
/// </para>
/// </remarks>
internal static class JoinSecretHasher
{
    /// <summary>Length of the random salt, in bytes.</summary>
    private const int SaltSize = 16;

    /// <summary>Length of the derived hash, in bytes.</summary>
    private const int HashSize = 32;

    /// <summary>
    /// PBKDF2 iteration count. Above OWASP's 2023 floor of 600,000 for SHA-256.
    /// </summary>
    /// <remarks>
    /// Stored in the encoded hash rather than read from here at verification time, so raising this
    /// does not invalidate secrets hashed under the old value.
    /// </remarks>
    private const int Iterations = 650_000;

    private const string Prefix = "pbkdf2-sha256";

    /// <summary>
    /// Hashes a joining password into the string form stored in configuration.
    /// </summary>
    /// <remarks>
    /// Format is <c>pbkdf2-sha256$iterations$salt$hash</c>, salt and hash base64. Self-describing so
    /// the iteration count can rise without stranding existing values.
    /// </remarks>
    /// <exception cref="ArgumentException">The secret is empty or whitespace.</exception>
    public static string Hash(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("A joining password must not be empty.", nameof(secret));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(secret, salt, Iterations);

        return string.Join('$', Prefix, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Verifies a presented password against a stored hash.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for anything malformed rather than throwing: the stored value
    /// comes from operator-edited configuration and the presented one from an unauthenticated
    /// caller, so neither is trusted to be well formed. A malformed stored hash refuses every
    /// attempt, which fails closed.
    /// </remarks>
    public static bool Verify(string? secret, string? storedHash)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Derive(secret, salt, iterations, expected.Length);

        // Constant-time: a byte-by-byte compare that returns early leaks how much of the hash an
        // attacker has matched, which is enough to reconstruct it one byte at a time.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string secret, byte[] salt, int iterations, int length = HashSize)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            length);
}
