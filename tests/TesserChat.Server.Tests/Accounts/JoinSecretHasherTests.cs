using TesserChat.Server.Accounts;

namespace TesserChat.Server.Tests.Accounts;

/// <summary>
/// Covers hashing and verification of the shared joining password (§5.2 mode 2).
/// </summary>
/// <remarks>
/// No database, so these run everywhere rather than skipping where Docker cannot serve containers.
/// </remarks>
public sealed class JoinSecretHasherTests
{
    [Fact]
    public void Verify_AcceptsTheCorrectSecret()
    {
        var hash = JoinSecretHasher.Hash("correct horse battery staple");

        Assert.True(JoinSecretHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_RejectsAWrongSecret()
    {
        var hash = JoinSecretHasher.Hash("correct horse battery staple");

        Assert.False(JoinSecretHasher.Verify("Correct Horse Battery Staple", hash));
        Assert.False(JoinSecretHasher.Verify("correct horse battery stapl", hash));
        Assert.False(JoinSecretHasher.Verify("", hash));
        Assert.False(JoinSecretHasher.Verify(null, hash));
    }

    [Fact]
    public void Hash_DoesNotContainTheSecret()
    {
        const string secret = "a-very-distinctive-passphrase";

        var hash = JoinSecretHasher.Hash(secret);

        // The stored value must not carry the password an operator could hand to someone else.
        Assert.DoesNotContain(secret, hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hash_IsSaltedSoTheSameSecretHashesDifferently()
    {
        var first = JoinSecretHasher.Hash("same secret");
        var second = JoinSecretHasher.Hash("same secret");

        Assert.NotEqual(first, second);

        // Both still verify: the salt travels in the encoded value.
        Assert.True(JoinSecretHasher.Verify("same secret", first));
        Assert.True(JoinSecretHasher.Verify("same secret", second));
    }

    [Fact]
    public void Hash_RefusesAnEmptySecret()
    {
        Assert.Throws<ArgumentException>(() => JoinSecretHasher.Hash(""));
        Assert.Throws<ArgumentException>(() => JoinSecretHasher.Hash("   "));
    }

    [Fact]
    public void Verify_FailsClosed_OnAMalformedStoredHash()
    {
        // Operator-edited configuration, so none of these is unthinkable. Every one must refuse
        // rather than throw or, worse, accept.
        foreach (var malformed in new[]
                 {
                     null,
                     "",
                     "   ",
                     "not-a-hash",
                     "pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==",
                     "pbkdf2-sha256$650000$not-base64!$aGFzaA==",
                     "pbkdf2-sha256$650000$c2FsdA==",
                     "argon2id$650000$c2FsdA==$aGFzaA==",
                     "pbkdf2-sha256$0$c2FsdA==$aGFzaA==",
                     "pbkdf2-sha256$650000$$aGFzaA==",
                 })
        {
            Assert.False(
                JoinSecretHasher.Verify("any secret", malformed),
                $"A malformed stored hash must refuse: {malformed ?? "<null>"}");
        }
    }

    [Fact]
    public void Verify_HonoursTheIterationCountInTheStoredValue()
    {
        var hash = JoinSecretHasher.Hash("secret");

        // The count travels in the value rather than being read from a constant, so raising the
        // constant later does not strand hashes produced under the old one.
        Assert.StartsWith("pbkdf2-sha256$", hash, StringComparison.Ordinal);

        var lowered = hash.Replace("$650000$", "$1000$", StringComparison.Ordinal);
        Assert.NotEqual(hash, lowered);

        // Re-hashed at 1000 iterations the digest differs, so this must not verify — proof the
        // count is genuinely used rather than ignored.
        Assert.False(JoinSecretHasher.Verify("secret", lowered));
    }
}
