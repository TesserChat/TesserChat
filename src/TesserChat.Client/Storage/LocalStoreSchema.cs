using Microsoft.Data.Sqlite;

namespace TesserChat.Client.Storage;

/// <summary>
/// The local database's schema, and the migrations that bring an older one up to it (§9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written migrations, not EF Core.</b> The server uses EF because a self-hoster's database
/// is long-lived and operator-managed (§5.4); this one is a local cache the app owns outright, and
/// EF would add a heavyweight dependency and a startup cost to a desktop app for a schema of a few
/// tables. The tradeoff is that migrations are written by hand and must be appended, never edited.
/// </para>
/// <para>
/// <b>Versioning uses SQLite's own <c>user_version</c> pragma.</b> A dedicated version table would
/// itself need creating before it could be read, which is the bootstrapping problem this pragma
/// exists to avoid: it is present on every SQLite database from the moment it exists, and reads as
/// 0 on a brand new one.
/// </para>
/// <para>
/// <b>Migrations are append-only.</b> Velopack updates the app without the user thinking about it
/// (§9.6), so an installed client will run these against a database written by an older version.
/// Editing a shipped migration means two users at the same version have different schemas — so a
/// change to the schema is always a new entry in <see cref="Migrations"/>, never an edit to an
/// existing one.
/// </para>
/// </remarks>
internal static class LocalStoreSchema
{
    /// <summary>
    /// The schema version this build expects, derived from how many migrations exist.
    /// </summary>
    /// <remarks>
    /// Derived rather than declared, so adding a migration cannot be half-done: there is no
    /// separate constant to forget to bump.
    /// </remarks>
    public static int CurrentVersion => Migrations.Length;

    /// <summary>
    /// Every migration, in order. Index 0 takes a new database to version 1.
    /// </summary>
    /// <remarks>
    /// <b>Append only.</b> See the note on this class: an edit here rewrites history for anyone who
    /// already ran the old version.
    /// </remarks>
    private static readonly string[] Migrations =
    [
        // v1 — the initial schema.
        """
        CREATE TABLE servers (
            id                  TEXT    NOT NULL PRIMARY KEY,
            address             TEXT    NOT NULL,
            name                TEXT    NOT NULL,
            account_id          TEXT    NULL,
            added_at            TEXT    NOT NULL,
            last_connected_at   TEXT    NULL
        );

        CREATE UNIQUE INDEX ix_servers_address ON servers (address);

        CREATE TABLE session_tokens (
            server_id   TEXT    NOT NULL PRIMARY KEY REFERENCES servers (id) ON DELETE CASCADE,
            token       TEXT    NOT NULL,
            account_id  TEXT    NOT NULL,
            expires_at  TEXT    NOT NULL
        );

        CREATE TABLE contacts (
            signing_key     TEXT    NOT NULL PRIMARY KEY,
            encryption_key  TEXT    NOT NULL,
            display_name    TEXT    NOT NULL,
            added_at        TEXT    NOT NULL,
            is_blocked      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE direct_messages (
            id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            peer_key        TEXT    NOT NULL,
            message_id      TEXT    NOT NULL,
            sent_by_me      INTEGER NOT NULL,
            sent_at         TEXT    NOT NULL,
            body            TEXT    NOT NULL,
            received_via    TEXT    NULL
        );

        CREATE UNIQUE INDEX ix_direct_messages_message_id
            ON direct_messages (message_id);

        CREATE INDEX ix_direct_messages_thread
            ON direct_messages (peer_key, id);
        """,
    ];

    /// <summary>
    /// Brings a database up to <see cref="CurrentVersion"/>, creating it if it is new.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every outstanding migration runs inside one transaction along with the version bump, so an
    /// interrupted upgrade leaves the database at its old version rather than half-migrated. A
    /// desktop app can be killed mid-write at any moment, which makes that the normal case to
    /// design for rather than an unlikely one.
    /// </para>
    /// <para>
    /// A database from a <i>newer</i> build is left alone and reported, rather than being opened
    /// and used. That happens when a user rolls back a version, and reading a future schema with
    /// today's queries would either fail obscurely or quietly write data the newer build then
    /// misreads.
    /// </para>
    /// </remarks>
    public static void Migrate(SqliteConnection connection)
        => MigrateTo(connection, CurrentVersion);

    /// <summary>
    /// Brings a database up to <paramref name="targetVersion"/> specifically.
    /// </summary>
    /// <remarks>
    /// Exists so a caller can reconstruct the schema as of an earlier version — which is what the
    /// upgrade tests need in order to start from a database an older build would have written,
    /// rather than from a hand-copied schema free to drift from the real migrations.
    /// </remarks>
    public static void MigrateTo(SqliteConnection connection, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegative(targetVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetVersion, CurrentVersion);

        var version = ReadVersion(connection);

        if (version > targetVersion)
        {
            throw new LocalStoreVersionException(version, targetVersion);
        }

        if (version == targetVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();

        for (var next = version; next < targetVersion; next++)
        {
            Execute(connection, transaction, Migrations[next]);
        }

        // Interpolated because PRAGMA does not accept a parameter. The value is an int this class
        // has already bounds-checked, never anything a caller supplies directly.
        Execute(connection, transaction, $"PRAGMA user_version = {targetVersion};");

        transaction.Commit();
    }

    /// <summary>The schema version recorded in the database. 0 for a new one.</summary>
    public static int ReadVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Thrown when the local database was written by a newer build than the one opening it (§9.5).
/// </summary>
/// <remarks>
/// Its own type so a caller can tell this apart from a corrupt or unreadable file: the remedy is to
/// update the app, not to discard the data, and only a typed exception lets the UI say so.
/// </remarks>
internal sealed class LocalStoreVersionException(int foundVersion, int supportedVersion)
    : InvalidOperationException(
        $"The local database is at schema version {foundVersion}, but this build supports "
        + $"{supportedVersion}. It was written by a newer version of the app.")
{
    /// <summary>The version found in the database.</summary>
    public int FoundVersion { get; } = foundVersion;

    /// <summary>The newest version this build understands.</summary>
    public int SupportedVersion { get; } = supportedVersion;
}
