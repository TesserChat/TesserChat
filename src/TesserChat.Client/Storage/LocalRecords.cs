namespace TesserChat.Client.Storage;

/// <summary>
/// A server the user has added (§9.5).
/// </summary>
/// <param name="Id">
/// The server's own id, as it reports at login (§4.7.2). Identity is the id rather than the
/// address: a server that moves host is the same server, and one that appears at a familiar address
/// with a different id is not.
/// </param>
/// <param name="Address">Where to reach it. Unique locally — the same server added twice is once.</param>
/// <param name="Name">What to show in the server rail (§9.2).</param>
/// <param name="AccountId">
/// The account this user holds there, once known. Null before a first successful login: a server
/// can be saved before it has ever been logged into.
/// </param>
/// <param name="AddedAt">When the user added it.</param>
/// <param name="LastConnectedAt">
/// When a connection last succeeded, or null if one never has. This is what §7.2 orders by when
/// choosing which shared server routes a DM.
/// </param>
internal sealed record KnownServer(
    Guid Id,
    string Address,
    string Name,
    Guid? AccountId,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastConnectedAt);

/// <summary>
/// A cached session token for one server (§4.7.6).
/// </summary>
/// <param name="ServerId">The server it authenticates against.</param>
/// <param name="Token">The bearer token.</param>
/// <param name="AccountId">The account it authenticates as.</param>
/// <param name="ExpiresAt">
/// When it stops being accepted. Stored so the client can re-authenticate before making a call that
/// would fail, rather than discovering expiry from a rejected request.
/// </param>
/// <remarks>
/// <b>These are cached, never depended on.</b> §4.7.6 has no refresh token precisely because
/// re-running challenge-response is cheap; a missing or expired row here costs one round trip
/// against the identity key, so deleting this table loses nothing but a little latency. That is
/// also what makes storing them at this sensitivity acceptable — a token lifted from this file
/// stops working within its lifetime, whereas the identity key never expires, which is why that
/// lives in OS-native secure storage instead (§4.2).
/// </remarks>
internal sealed record CachedSession(
    Guid ServerId,
    string Token,
    Guid AccountId,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Someone the user has saved, and whether they are blocked (§8.1, §7.5.1).
/// </summary>
/// <param name="SigningKey">
/// Their Ed25519 public key, base64url. The identity, and the primary key: §8.1 adds contacts by
/// public key, so this is what a contact <i>is</i> rather than an attribute of one.
/// </param>
/// <param name="EncryptionKey">Their X25519 public key, base64url, for deriving a DM secret (§7.1).</param>
/// <param name="DisplayName">A local label the user chooses. Never an identifier.</param>
/// <param name="AddedAt">When they were saved.</param>
/// <param name="IsBlocked">
/// Whether their messages are dropped after decryption (§7.5.1). Held on the contact row rather
/// than a separate block list, so a key cannot be simultaneously known-and-unblocked in one table
/// and blocked in another.
/// </param>
/// <remarks>
/// Blocking a key that was never a contact still creates a row here — the row is what records the
/// decision, and §7.5.2's first-contact prompt offers Block as one of its three actions.
/// </remarks>
internal sealed record Contact(
    string SigningKey,
    string EncryptionKey,
    string DisplayName,
    DateTimeOffset AddedAt,
    bool IsBlocked);

/// <summary>
/// One direct message, as stored locally (§7.3).
/// </summary>
/// <param name="Id">Local row id. Orders a thread; assigned by SQLite.</param>
/// <param name="PeerKey">
/// The <i>other</i> person's signing key, base64url — whether they sent it or received it. This is
/// what makes a thread a thread: §7.3 keys DM history by the peer rather than by which server
/// relayed it, so a conversation reads as one continuous thread even after the pair move to a
/// different shared server.
/// </param>
/// <param name="MessageId">
/// The sender's own id for this message, unique across the store. What §7.4's dedup is built on: the
/// same message fanned out through several servers arrives more than once and must be shown once.
/// </param>
/// <param name="SentByMe">Which direction it went.</param>
/// <param name="SentAt">When the sender says they sent it.</param>
/// <param name="Body">The plaintext, decrypted before it ever reached this table.</param>
/// <param name="ReceivedVia">
/// Which server relayed it, if known. Recorded for display and diagnosis only — deliberately not
/// part of what identifies the message or its thread, for the reason in <paramref name="PeerKey"/>.
/// </param>
internal sealed record DirectMessage(
    long Id,
    string PeerKey,
    string MessageId,
    bool SentByMe,
    DateTimeOffset SentAt,
    string Body,
    Guid? ReceivedVia);
