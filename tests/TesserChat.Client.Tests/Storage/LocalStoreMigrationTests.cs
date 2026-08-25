using Microsoft.Data.Sqlite;
using TesserChat.Client.Storage;

namespace TesserChat.Client.Tests.Storage;

/// <summary>
/// Covers opening and upgrading the local database on disk (§9.5, §9.6).
/// </summary>
/// <remarks>
/// Against real files rather than in-memory databases, because the property under test is what
/// happens to data that was already there — and an in-memory database cannot be closed and reopened,
/// which is exactly the sequence an app update performs.
/// </remarks>
public sealed class LocalStoreMigrationTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"tesserchat-tests-{Guid.NewGuid():N}");

    public LocalStoreMigrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ANewDatabase_IsCreatedAtTheCurrentVersion()
    {
        using var store = LocalStore.Open(PathFor("new.db"));

        Assert.Equal(LocalStoreSchema.CurrentVersion, store.SchemaVersion);
    }

    [Fact]
    public void OpeningTwice_PreservesWhatWasStored()
    {
        var path = PathFor("reopen.db");
        var serverId = Guid.NewGuid();

        using (var store = LocalStore.Open(path))
        {
            store.SaveServer(new KnownServer(
                serverId, "https://a.example", "Example", null, Now, null));
            store.TryAddMessage(new DirectMessage(
                0, "peer", "m1", false, Now, "Survives a restart.", null));
        }

        // What every app launch after the first does.
        using (var reopened = LocalStore.Open(path))
        {
            Assert.Equal(serverId, Assert.Single(reopened.GetServers()).Id);
            Assert.Equal(
                "Survives a restart.",
                Assert.Single(reopened.GetThread("peer")).Body);
        }
    }

    [Fact]
    public void MigratingAnAlreadyCurrentDatabase_ChangesNothing()
    {
        var path = PathFor("idempotent.db");

        using (var store = LocalStore.Open(path))
        {
            store.SaveContact(new Contact("sign-key", "encrypt-key", "Ada", Now, false));
        }

        using (var reopened = LocalStore.Open(path))
        {
            // Reopening runs migration again, which must be a no-op rather than a re-create.
            Assert.Equal(LocalStoreSchema.CurrentVersion, reopened.SchemaVersion);
            Assert.Equal("Ada", Assert.Single(reopened.GetContacts()).DisplayName);
        }
    }

    [Fact]
    public void AnEmptyFile_IsMigratedToTheCurrentSchema()
    {
        var path = PathFor("from-empty.db");

        // A file that exists but holds no schema — version 0, where every install starts.
        using (var connection = Connect(path))
        {
            connection.Open();
        }

        using var store = LocalStore.Open(path);

        Assert.Equal(LocalStoreSchema.CurrentVersion, store.SchemaVersion);

        // Every table the current schema declares is usable, so no migration was skipped.
        store.SaveServer(new KnownServer(
            Guid.NewGuid(), "https://a.example", "Example", null, Now, null));
        store.SaveContact(new Contact("sign-key", "encrypt-key", "Ada", Now, false));
        store.TryAddMessage(new DirectMessage(0, "peer", "m1", false, Now, "Stored.", null));

        Assert.Single(store.GetServers());
        Assert.Single(store.GetContacts());
        Assert.Single(store.GetThread("peer"));
    }

    [Fact]
    public void AnUpgrade_PreservesDataWrittenByTheOlderSchema()
    {
        var path = PathFor("upgrade.db");
        var serverId = Guid.NewGuid();

        // A database as an older build left it: only the migrations that existed then, stamped
        // with that version. Built by running the real migrations to that point rather than by
        // hand, so it cannot drift from what that build actually produced.
        using (var connection = Connect(path))
        {
            connection.Open();
            LocalStoreSchema.MigrateTo(connection, 1);

            Execute(
                connection,
                "INSERT INTO servers (id, address, name, account_id, added_at, last_connected_at) "
                + "VALUES ($id, 'https://a.example', 'Example', NULL, $now, NULL);",
                ("$id", serverId.ToString("D")),
                ("$now", Now.ToString("O")));

            Execute(
                connection,
                "INSERT INTO direct_messages "
                + "(peer_key, message_id, sent_by_me, sent_at, body, received_via) "
                + "VALUES ('peer', 'm1', 0, $now, 'Written by the old version.', NULL);",
                ("$now", Now.ToString("O")));
        }

        using var upgraded = LocalStore.Open(path);

        // The point of the test: an app updated underneath the user (§9.6) keeps their data.
        Assert.Equal(LocalStoreSchema.CurrentVersion, upgraded.SchemaVersion);
        Assert.Equal(serverId, Assert.Single(upgraded.GetServers()).Id);
        Assert.Equal(
            "Written by the old version.",
            Assert.Single(upgraded.GetThread("peer")).Body);
    }

    [Fact]
    public void ADatabaseFromANewerBuild_IsRefusedRatherThanOpened()
    {
        var path = PathFor("from-the-future.db");

        using (var store = LocalStore.Open(path))
        {
            store.SaveContact(new Contact("sign-key", "encrypt-key", "Ada", Now, false));
        }

        SetVersion(path, LocalStoreSchema.CurrentVersion + 1);

        // What a version rollback produces. Reading a future schema with today's queries would
        // either fail obscurely or quietly write data the newer build then misreads, so this is
        // refused with a typed exception the UI can act on — the remedy is to update the app, not
        // to discard the data.
        var error = Assert.Throws<LocalStoreVersionException>(() => LocalStore.Open(path));

        Assert.Equal(LocalStoreSchema.CurrentVersion + 1, error.FoundVersion);
        Assert.Equal(LocalStoreSchema.CurrentVersion, error.SupportedVersion);

        // And the data is still there, untouched, for the newer build to read.
        SetVersion(path, LocalStoreSchema.CurrentVersion);
        using var reopened = LocalStore.Open(path);
        Assert.Single(reopened.GetContacts());
    }

    [Fact]
    public void TheDatabasePath_IsUnderThePerUserApplicationDataDirectory()
    {
        var path = LocalStoreLocation.GetDatabasePath();

        // Not beside the executable: Velopack replaces that directory on update (§9.6).
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(applicationData, path, StringComparison.Ordinal);
        Assert.EndsWith(LocalStoreLocation.FileName, path, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a passing test over.
        }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    private static SqliteConnection Connect(string path)
        => new(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);

    private static void Execute(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>Rewrites the schema version recorded in a database file.</summary>
    private static void SetVersion(string path, int version)
    {
        using var connection = Connect(path);
        connection.Open();

        Execute(connection, $"PRAGMA user_version = {version};");
    }
}
