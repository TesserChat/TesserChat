using TesserChat.Shared.Identity;

namespace TesserChat.Server.Persistence;

/// <summary>
/// One member of this server: the identity → account mapping the server keeps (§5.1).
/// </summary>
/// <remarks>
/// <para>
/// The server stores public keys only. Nothing here is secret, and nothing here can be used to
/// impersonate the account — authentication is possession of the matching private key, proven by
/// signing a challenge (§4.7), never by presenting a row from this table.
/// </para>
/// <para>
/// Both public keys are held together. The Ed25519 key is what logins are verified against; the
/// X25519 key is published so a DM partner can derive a shared secret without a directory service
/// (§7.1). Storing them on one row is what makes "look up this account" answer both questions at
/// once, and removes any way for the two to be paired wrongly.
/// </para>
/// </remarks>
internal sealed class Account
{
    /// <summary>
    /// Longest display name a member may choose.
    /// </summary>
    /// <remarks>
    /// A bound rather than a considered maximum: display names are rendered in member lists and
    /// message headers, and an unbounded string is a storage and layout problem. Raising it later
    /// is a widening migration, which Postgres takes without a rewrite.
    /// </remarks>
    public const int DisplayNameMaxLength = 64;

    /// <summary>
    /// The permanent account id, derived from <see cref="SigningKey"/> by
    /// <see cref="AccountId.FromPublicKey"/>.
    /// </summary>
    /// <remarks>
    /// Derived, never generated: the same public key must resolve to the same account on this
    /// server forever, including after a database restore. That is also why the column has no value
    /// generation — the value comes from the key, not from Postgres.
    /// </remarks>
    public Guid Id { get; init; }

    /// <summary>Raw 32-byte Ed25519 public key. Unique across the server, and immutable.</summary>
    public byte[] SigningKey { get; init; } = [];

    /// <summary>Raw 32-byte X25519 public key, published for DM key exchange (§7.1).</summary>
    public byte[] EncryptionKey { get; init; } = [];

    /// <summary>
    /// The name shown to other members. Cosmetic, freely changeable, and never an identifier.
    /// </summary>
    /// <remarks>
    /// Deliberately not unique. Two members may choose the same display name; they remain distinct
    /// accounts because identity is <see cref="Id"/>. Nothing internal — permissions, authorship,
    /// the audit trail — may key off this value.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>When this key first registered on this server.</summary>
    public DateTimeOffset RegisteredAt { get; init; }

    /// <summary>
    /// Rebuilds the public identity this account was registered from.
    /// </summary>
    /// <remarks>
    /// Useful wherever the crypto helpers in <c>TesserChat.Shared</c> are needed — signature
    /// verification during login, for instance — so callers do not re-pair the two key columns by
    /// hand each time.
    /// </remarks>
    public PublicIdentity ToPublicIdentity() => new(SigningKey, EncryptionKey);
}
