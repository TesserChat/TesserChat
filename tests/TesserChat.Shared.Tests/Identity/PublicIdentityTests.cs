using TesserChat.Shared.Identity;

namespace TesserChat.Shared.Tests.Identity;

/// <summary>
/// Covers the public half of an identity: the shareable token (§8.1), the displayed fingerprint
/// (§4.2 step 3), and identity equality.
/// </summary>
public sealed class PublicIdentityTests
{
    [Fact]
    public void ShareableString_RoundTrips()
    {
        using var identity = IdentityKeyPair.Generate();

        var token = identity.Public.ToShareableString();

        Assert.True(PublicIdentity.TryParse(token, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(identity.Public, parsed);
        Assert.Equal(identity.Public.AccountId, parsed.AccountId);
    }

    [Fact]
    public void ShareableString_CarriesBothKeys()
    {
        using var identity = IdentityKeyPair.Generate();

        PublicIdentity.TryParse(identity.Public.ToShareableString(), out var parsed);

        // A friend token that dropped the encryption key would produce a contact you cannot DM.
        Assert.NotNull(parsed);
        Assert.True(identity.Public.SigningKey.SequenceEqual(parsed.SigningKey));
        Assert.True(identity.Public.EncryptionKey.SequenceEqual(parsed.EncryptionKey));
    }

    [Fact]
    public void ShareableString_IsUrlSafe()
    {
        using var identity = IdentityKeyPair.Generate();

        var token = identity.Public.ToShareableString();

        // These tokens get pasted into chat messages, URLs, and filenames — base64url avoids the
        // '+', '/', and '=' characters that would need escaping in any of those.
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void TryParse_ToleratesSurroundingWhitespace()
    {
        using var identity = IdentityKeyPair.Generate();

        // Hand-pasted tokens routinely arrive with a stray newline or trailing space.
        var token = $"  {identity.Public.ToShareableString()}\r\n";

        Assert.True(PublicIdentity.TryParse(token, out var parsed));
        Assert.Equal(identity.Public, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!not base64!!!")]
    [InlineData("dGVzdA")]                          // valid base64url, wrong length
    public void TryParse_RejectsMalformedTokens(string? token)
    {
        Assert.False(PublicIdentity.TryParse(token, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_RejectsATruncatedToken()
    {
        using var identity = IdentityKeyPair.Generate();
        var token = identity.Public.ToShareableString();

        Assert.False(PublicIdentity.TryParse(token[..^4], out _));
    }

    [Fact]
    public void Constructor_RejectsWrongLengthKeys()
    {
        var valid = new byte[IdentityKeyPair.PublicKeySize];

        Assert.Throws<ArgumentException>(() => new PublicIdentity(new byte[31], valid));
        Assert.Throws<ArgumentException>(() => new PublicIdentity(valid, new byte[33]));
    }

    [Fact]
    public void Constructor_CopiesTheKeyBytes()
    {
        using var identity = IdentityKeyPair.Generate();
        var signing = identity.Public.SigningKey.ToArray();
        var encryption = identity.Public.EncryptionKey.ToArray();

        var copy = new PublicIdentity(signing, encryption);
        var originalId = copy.AccountId;

        // Mutating the caller's array must not retroactively change an identity already handed out.
        signing[0] ^= 0xFF;

        Assert.Equal(originalId, copy.AccountId);
        Assert.False(copy.SigningKey.SequenceEqual(signing));
    }

    [Fact]
    public void Equals_IsTrueForTheSameKeys()
    {
        using var identity = IdentityKeyPair.Generate();

        var a = new PublicIdentity(identity.Public.SigningKey, identity.Public.EncryptionKey);
        var b = new PublicIdentity(identity.Public.SigningKey, identity.Public.EncryptionKey);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_IsFalseForDifferentIdentities()
    {
        using var first = IdentityKeyPair.Generate();
        using var second = IdentityKeyPair.Generate();

        Assert.NotEqual(first.Public, second.Public);
    }

    [Fact]
    public void Equals_IsFalseWhenOnlyTheEncryptionKeyDiffers()
    {
        using var first = IdentityKeyPair.Generate();
        using var second = IdentityKeyPair.Generate();

        // Same signing key, different encryption key: a substituted encryption key would silently
        // redirect DMs, so equality must not ignore that half.
        var spliced = new PublicIdentity(first.Public.SigningKey, second.Public.EncryptionKey);

        Assert.NotEqual(first.Public, spliced);
    }

    [Fact]
    public void Equals_IsFalseForNullAndOtherTypes()
    {
        using var identity = IdentityKeyPair.Generate();

        Assert.False(identity.Public.Equals(null));
        Assert.False(identity.Public.Equals("not an identity"));
    }

    [Fact]
    public void Fingerprint_IsStableAndGrouped()
    {
        using var identity = IdentityKeyPair.Generate();

        var fingerprint = identity.Public.ToFingerprint();

        // 32 key bytes -> 64 hex chars -> 16 groups of 4, separated by 15 spaces.
        Assert.Equal(16, fingerprint.Split(' ').Length);
        Assert.All(fingerprint.Split(' '), group => Assert.Equal(4, group.Length));
        Assert.Equal(fingerprint, identity.Public.ToFingerprint());
    }

    [Fact]
    public void Fingerprint_DiffersBetweenIdentities()
    {
        using var first = IdentityKeyPair.Generate();
        using var second = IdentityKeyPair.Generate();

        Assert.NotEqual(first.Public.ToFingerprint(), second.Public.ToFingerprint());
    }

    [Fact]
    public void Fingerprint_ReflectsTheSigningKey()
    {
        using var identity = IdentityKeyPair.Generate();

        var expected = Convert.ToHexStringLower(identity.Public.SigningKey);
        var actual = identity.Public.ToFingerprint().Replace(" ", string.Empty);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ToString_ContainsTheAccountIdAndNoKeyMaterial()
    {
        using var identity = IdentityKeyPair.Generate();

        var text = identity.Public.ToString();

        // ToString lands in logs; it must identify the account without dumping raw keys.
        Assert.Contains(AccountId.ToCanonicalString(identity.Public.AccountId), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(identity.Public.SigningKey), text, StringComparison.Ordinal);
    }
}
