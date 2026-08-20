using TesserChat.Shared.Identity;

namespace TesserChat.Shared.Tests.Identity;

/// <summary>
/// Covers the public-key → account UUID derivation.
/// </summary>
/// <remarks>
/// Determinism is the property under test throughout. This id is an account's permanent primary
/// key on a server — if derivation ever drifts, every existing account silently orphans, so these
/// tests are as much a change detector as a correctness check.
/// </remarks>
public sealed class AccountIdTests
{
    [Fact]
    public void FromPublicKey_IsDeterministic()
    {
        using var identity = IdentityKeyPair.Generate();

        var first = AccountId.FromPublicKey(identity.Public.SigningKey);
        var second = AccountId.FromPublicKey(identity.Public.SigningKey);

        Assert.Equal(first, second);
    }

    [Fact]
    public void FromPublicKey_DiffersBetweenIdentities()
    {
        using var first = IdentityKeyPair.Generate();
        using var second = IdentityKeyPair.Generate();

        Assert.NotEqual(
            AccountId.FromPublicKey(first.Public.SigningKey),
            AccountId.FromPublicKey(second.Public.SigningKey));
    }

    [Fact]
    public void FromPublicKey_ChangesWhenASingleBitOfTheKeyChanges()
    {
        using var identity = IdentityKeyPair.Generate();
        var key = identity.Public.SigningKey.ToArray();
        var original = AccountId.FromPublicKey(key);

        key[0] ^= 0x01;

        Assert.NotEqual(original, AccountId.FromPublicKey(key));
    }

    [Fact]
    public void FromPublicKey_MatchesTheIdOnThePublicIdentity()
    {
        using var identity = IdentityKeyPair.Generate();

        // PublicIdentity derives its id internally; that must not diverge from the standalone call.
        Assert.Equal(AccountId.FromPublicKey(identity.Public.SigningKey), identity.Public.AccountId);
    }

    [Fact]
    public void FromPublicKey_ProducesAKnownValueForAKnownKey()
    {
        // A pinned vector. If the domain separator, hash, truncation, or byte order ever changes,
        // this fails — which is the point. Changing derivation orphans every existing account, so
        // a deliberate change means updating this constant and accepting a breaking migration.
        //
        // Independently reproducible, so this pins the documented construction rather than merely
        // echoing whatever the implementation happens to emit:
        //   sha256(b"tesserchat:account-id:v1" + bytes(32))[:16]
        //   with byte 6 -> (b & 0x0F) | 0x80  (version 8)
        //        byte 8 -> (b & 0x3F) | 0x80  (RFC 4122 variant)
        //   rendered big-endian.
        var key = Convert.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000000");

        var id = AccountId.FromPublicKey(key);

        Assert.Equal("0081883d-e603-8b21-adbf-7878f9fd1591", AccountId.ToCanonicalString(id));
    }

    [Fact]
    public void FromPublicKey_SetsTheUuidVersionAndVariantBits()
    {
        using var identity = IdentityKeyPair.Generate();

        var bytes = AccountId.FromPublicKey(identity.Public.SigningKey).ToByteArray(bigEndian: true);

        Assert.Equal(0x80, bytes[6] & 0xF0);       // version 8 (custom)
        Assert.Equal(0x80, bytes[8] & 0xC0);       // RFC 4122 variant
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void FromPublicKey_RejectsAWrongLengthKey(int keyLength)
    {
        Assert.Throws<ArgumentException>(() => AccountId.FromPublicKey(new byte[keyLength]));
    }

    [Fact]
    public void ToCanonicalString_RoundTripsThroughTryParse()
    {
        using var identity = IdentityKeyPair.Generate();
        var id = identity.Public.AccountId;

        var text = AccountId.ToCanonicalString(id);

        Assert.True(AccountId.TryParse(text, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void ToCanonicalString_IsLowercaseAndHyphenated()
    {
        using var identity = IdentityKeyPair.Generate();

        var text = AccountId.ToCanonicalString(identity.Public.AccountId);

        Assert.Equal(36, text.Length);
        Assert.Equal(text.ToLowerInvariant(), text);
        Assert.Equal(4, text.Count(c => c == '-'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uuid")]
    [InlineData("{d7dcd6ec-e0e8-8c86-a3c8-9247ed5cdb52}")]  // braced form is not canonical
    [InlineData("d7dcd6ece0e88c86a3c89247ed5cdb52")]        // unhyphenated form is not canonical
    public void TryParse_RejectsMalformedInput(string? text)
    {
        Assert.False(AccountId.TryParse(text, out _));
    }
}
