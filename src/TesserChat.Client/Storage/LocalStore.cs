using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TesserChat.Client.Storage;

/// <summary>
/// The client's local database: known servers, cached tokens, contacts, and DM history (§9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Private keys never reach this file.</b> Identity keys live in OS-native secure storage
/// (§4.2), and keeping that split honest is the point of this class — everything here is either
/// public (a contact's keys), low-value and short-lived (a session token, §4.7.6), or already
/// plaintext on this machine by necessity (decrypted DM history). Nothing here can impersonate the
/// user. A reviewer adding a column should ask which of those three it is; if it is none of them,
/// it does not belong in this file.
/// </para>
/// <para>
/// <b>Foreign keys are enabled per connection.</b> SQLite defaults them off for backwards
/// compatibility, so the cascade from <c>servers</c> to <c>session_tokens</c> only holds because
/// this class turns them on every time it opens a connection.
/// </para>
/// <para>
/// <b>Timestamps are stored as round-trippable UTC strings.</b> SQLite has no date type, and text
/// in a fixed format stays readable in any SQLite browser — which matters for a store chosen partly
/// for its inspectability (§9.5).
/// </para>
/// <para>
/// This class is not thread-safe and is not intended to be shared across threads: one connection is
/// held open for the object's lifetime, and SQLite serialises writes to a file anyway.
/// </para>
/// </remarks>
internal sealed class LocalStore : IDisposable
{
    /// <summary>Round-trip format, so a parsed timestamp is the instant that was written.</summary>
    private const string TimestampFormat = "O";

    private readonly SqliteConnection _connection;

    private LocalStore(SqliteConnection connection) => _connection = connection;

    /// <summary>
    /// Opens (creating if needed) the database at <paramref name="path"/> and migrates it.
    /// </summary>
    /// <remarks>
    /// Migration on open rather than as a separate step a caller must remember: an app updated by
    /// Velopack (§9.6) starts against whatever schema the last version left, and there is no
    /// correct moment to use this store before it has been brought up to date.
    /// </remarks>
    public static LocalStore Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString);

        connection.Open();

        try
        {
            EnableForeignKeys(connection);
            LocalStoreSchema.Migrate(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new LocalStore(connection);
    }

    /// <summary>Opens a private in-memory database, for tests.</summary>
    /// <remarks>
    /// The connection has to stay open for the database to exist at all — an in-memory SQLite
    /// database lives exactly as long as its connection — which is why this returns a store holding
    /// one rather than a path.
    /// </remarks>
    public static LocalStore OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        EnableForeignKeys(connection);
        LocalStoreSchema.Migrate(connection);

        return new LocalStore(connection);
    }

    /// <summary>The schema version of the open database.</summary>
    public int SchemaVersion => LocalStoreSchema.ReadVersion(_connection);

    // --- Servers -------------------------------------------------------------------------------

    /// <summary>
    /// Saves a server, or updates the one already stored at that address.
    /// </summary>
    /// <remarks>
    /// Keyed on <see cref="KnownServer.Id"/>, so re-adding a server the user already has updates it
    /// rather than duplicating it. The address carries a unique index too: the same server reached
    /// by two spellings of its address would otherwise appear twice in the server rail.
    /// </remarks>
    public void SaveServer(KnownServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO servers (id, address, name, account_id, added_at, last_connected_at)
            VALUES ($id, $address, $name, $accountId, $addedAt, $lastConnectedAt)
            ON CONFLICT (id) DO UPDATE SET
                address           = excluded.address,
                name              = excluded.name,
                account_id        = excluded.account_id,
                last_connected_at = excluded.last_connected_at;
            """;

        command.Parameters.AddWithValue("$id", server.Id.ToString("D"));
        command.Parameters.AddWithValue("$address", server.Address);
        command.Parameters.AddWithValue("$name", server.Name);
        command.Parameters.AddWithValue("$accountId", ToDb(server.AccountId));
        command.Parameters.AddWithValue("$addedAt", ToDb(server.AddedAt));
        command.Parameters.AddWithValue("$lastConnectedAt", ToDb(server.LastConnectedAt));

        command.ExecuteNonQuery();
    }

    /// <summary>Every saved server, most recently connected first, then by name.</summary>
    /// <remarks>
    /// That order is what §7.2 wants when defaulting a DM's route to the most recently active shared
    /// server, and it puts a server never connected to at the end rather than the front.
    /// </remarks>
    public IReadOnlyList<KnownServer> GetServers()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, address, name, account_id, added_at, last_connected_at
            FROM servers
            ORDER BY last_connected_at IS NULL, last_connected_at DESC, name;
            """;

        var servers = new List<KnownServer>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            servers.Add(new KnownServer(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                ReadNullableGuid(reader, 3),
                ReadTimestamp(reader, 4),
                ReadNullableTimestamp(reader, 5)));
        }

        return servers;
    }

    /// <summary>Records that a connection to a server just succeeded.</summary>
    public void MarkConnected(Guid serverId, DateTimeOffset at)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE servers SET last_connected_at = $at WHERE id = $id;";

        command.Parameters.AddWithValue("$id", serverId.ToString("D"));
        command.Parameters.AddWithValue("$at", ToDb(at));

        command.ExecuteNonQuery();
    }

    /// <summary>Forgets a server, and the cached token that went with it.</summary>
    /// <remarks>
    /// The token goes by cascade rather than by a second statement here, so it cannot be left
    /// behind by a caller that forgets — a token outliving the server it authenticates against is
    /// exactly the kind of orphan that is never noticed.
    /// </remarks>
    public void RemoveServer(Guid serverId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM servers WHERE id = $id;";
        command.Parameters.AddWithValue("$id", serverId.ToString("D"));

        command.ExecuteNonQuery();
    }

    // --- Session tokens ------------------------------------------------------------------------

    /// <summary>Caches the session token for a server, replacing any previous one.</summary>
    public void SaveSession(CachedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO session_tokens (server_id, token, account_id, expires_at)
            VALUES ($serverId, $token, $accountId, $expiresAt)
            ON CONFLICT (server_id) DO UPDATE SET
                token      = excluded.token,
                account_id = excluded.account_id,
                expires_at = excluded.expires_at;
            """;

        command.Parameters.AddWithValue("$serverId", session.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$token", session.Token);
        command.Parameters.AddWithValue("$accountId", session.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$expiresAt", ToDb(session.ExpiresAt));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The cached token for a server, if one is stored and has not expired.
    /// </summary>
    /// <param name="serverId">The server to look up.</param>
    /// <param name="now">The current time, for the expiry check.</param>
    /// <remarks>
    /// An expired token is reported as absent rather than returned for the caller to check, because
    /// the two cases have the same remedy — re-run challenge-response (§4.7.6) — and a caller that
    /// forgot to check would send a token the server is certain to reject.
    /// </remarks>
    public CachedSession? GetSession(Guid serverId, DateTimeOffset now)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT server_id, token, account_id, expires_at
            FROM session_tokens
            WHERE server_id = $serverId;
            """;

        command.Parameters.AddWithValue("$serverId", serverId.ToString("D"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var session = new CachedSession(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Guid.Parse(reader.GetString(2)),
            ReadTimestamp(reader, 3));

        return session.ExpiresAt > now ? session : null;
    }

    /// <summary>Discards a server's cached token.</summary>
    /// <remarks>
    /// What a sign-out does. The identity key is untouched, so signing back in is a round trip
    /// rather than a recovery.
    /// </remarks>
    public void ClearSession(Guid serverId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM session_tokens WHERE server_id = $serverId;";
        command.Parameters.AddWithValue("$serverId", serverId.ToString("D"));

        command.ExecuteNonQuery();
    }

    // --- Contacts ------------------------------------------------------------------------------

    /// <summary>Saves a contact, or updates the one already stored under that key.</summary>
    public void SaveContact(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO contacts (signing_key, encryption_key, display_name, added_at, is_blocked)
            VALUES ($signingKey, $encryptionKey, $displayName, $addedAt, $isBlocked)
            ON CONFLICT (signing_key) DO UPDATE SET
                encryption_key = excluded.encryption_key,
                display_name   = excluded.display_name,
                is_blocked     = excluded.is_blocked;
            """;

        command.Parameters.AddWithValue("$signingKey", contact.SigningKey);
        command.Parameters.AddWithValue("$encryptionKey", contact.EncryptionKey);
        command.Parameters.AddWithValue("$displayName", contact.DisplayName);
        command.Parameters.AddWithValue("$addedAt", ToDb(contact.AddedAt));
        command.Parameters.AddWithValue("$isBlocked", contact.IsBlocked ? 1 : 0);

        command.ExecuteNonQuery();
    }

    /// <summary>Every saved contact, by display name.</summary>
    public IReadOnlyList<Contact> GetContacts()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT signing_key, encryption_key, display_name, added_at, is_blocked
            FROM contacts
            ORDER BY display_name;
            """;

        var contacts = new List<Contact>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            contacts.Add(ReadContact(reader));
        }

        return contacts;
    }

    /// <summary>One contact by signing key, or null if that key is not saved.</summary>
    public Contact? GetContact(string signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT signing_key, encryption_key, display_name, added_at, is_blocked
            FROM contacts
            WHERE signing_key = $signingKey;
            """;

        command.Parameters.AddWithValue("$signingKey", signingKey);

        using var reader = command.ExecuteReader();

        return reader.Read() ? ReadContact(reader) : null;
    }

    /// <summary>
    /// Whether a key is blocked (§7.5.1).
    /// </summary>
    /// <remarks>
    /// A key that is not a contact at all is not blocked, which is the same answer as a contact
    /// explicitly unblocked. That is correct: §7.5.2 sends an unknown sender to the first-contact
    /// prompt, which is a different path from a blocked one, and the caller distinguishes them by
    /// asking whether the contact exists rather than by this returning something other than false.
    /// </remarks>
    public bool IsBlocked(string signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);

        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT is_blocked FROM contacts WHERE signing_key = $signingKey;";

        command.Parameters.AddWithValue("$signingKey", signingKey);

        var value = command.ExecuteScalar();

        return value is not null
            && value is not DBNull
            && Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Removes a contact entirely.</summary>
    /// <remarks>
    /// Deliberately does not touch <c>direct_messages</c>: forgetting who someone is should not
    /// silently delete the conversation with them. Deleting history is its own action.
    /// </remarks>
    public void RemoveContact(string signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);

        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM contacts WHERE signing_key = $signingKey;";
        command.Parameters.AddWithValue("$signingKey", signingKey);

        command.ExecuteNonQuery();
    }

    // --- Direct messages -----------------------------------------------------------------------

    /// <summary>
    /// Stores a message if it is not already stored, and says whether it was new.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if a message with this id was already held — §7.4's dedup case, where
    /// the same message arrives through several fanned-out servers. The caller still acknowledges a
    /// duplicate to the relaying server, or that server keeps it queued forever.
    /// </returns>
    /// <remarks>
    /// Dedup is on the sender's message id rather than on a timestamp, because §7.4 notes two
    /// distinct messages can share a millisecond. The unique index is what enforces it — a check
    /// followed by an insert would be a race between two arrivals of the same fanned-out message.
    /// </remarks>
    public bool TryAddMessage(DirectMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO direct_messages
                (peer_key, message_id, sent_by_me, sent_at, body, received_via)
            VALUES ($peerKey, $messageId, $sentByMe, $sentAt, $body, $receivedVia)
            ON CONFLICT (message_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("$peerKey", message.PeerKey);
        command.Parameters.AddWithValue("$messageId", message.MessageId);
        command.Parameters.AddWithValue("$sentByMe", message.SentByMe ? 1 : 0);
        command.Parameters.AddWithValue("$sentAt", ToDb(message.SentAt));
        command.Parameters.AddWithValue("$body", message.Body);
        command.Parameters.AddWithValue("$receivedVia", ToDb(message.ReceivedVia));

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// The conversation with one peer, oldest first.
    /// </summary>
    /// <remarks>
    /// Keyed by peer alone, never by server: §7.3 requires that a conversation reads as one thread
    /// even if the pair later message through a different shared server, and querying by peer is
    /// what delivers that rather than a merge at display time.
    /// </remarks>
    public IReadOnlyList<DirectMessage> GetThread(string peerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerKey);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, peer_key, message_id, sent_by_me, sent_at, body, received_via
            FROM direct_messages
            WHERE peer_key = $peerKey
            ORDER BY id;
            """;

        command.Parameters.AddWithValue("$peerKey", peerKey);

        var messages = new List<DirectMessage>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(new DirectMessage(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                ReadTimestamp(reader, 4),
                reader.GetString(5),
                ReadNullableGuid(reader, 6)));
        }

        return messages;
    }

    /// <summary>Every peer key there is a conversation with.</summary>
    public IReadOnlyList<string> GetThreadPeers()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT peer_key, MAX(id) AS latest
            FROM direct_messages
            GROUP BY peer_key
            ORDER BY latest DESC;
            """;

        var peers = new List<string>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            peers.Add(reader.GetString(0));
        }

        return peers;
    }

    /// <summary>Deletes the conversation with one peer.</summary>
    public void DeleteThread(string peerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerKey);

        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM direct_messages WHERE peer_key = $peerKey;";
        command.Parameters.AddWithValue("$peerKey", peerKey);

        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private static Contact ReadContact(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ReadTimestamp(reader, 3),
            reader.GetInt64(4) != 0);

    private static object ToDb(DateTimeOffset value)
        => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static object ToDb(DateTimeOffset? value)
        => value is null ? DBNull.Value : ToDb(value.Value);

    private static object ToDb(Guid? value)
        => value is null ? DBNull.Value : value.Value.ToString("D");

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader, ordinal);

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
}
