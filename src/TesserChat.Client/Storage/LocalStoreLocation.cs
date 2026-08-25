namespace TesserChat.Client.Storage;

/// <summary>
/// Where the local database lives on each platform (§9.5).
/// </summary>
/// <remarks>
/// <para>
/// Under the OS's per-user application-data directory rather than beside the executable: Velopack
/// installs the app into a versioned directory and replaces it on update (§9.6), so anything stored
/// next to the binary is data the next update walks over.
/// </para>
/// <para>
/// <see cref="Environment.SpecialFolder.ApplicationData"/> resolves per platform on its own —
/// <c>%APPDATA%</c> on Windows, <c>~/Library/Application Support</c> on macOS, and
/// <c>$XDG_CONFIG_HOME</c> or <c>~/.config</c> on Linux. It is deliberately the roaming variant on
/// Windows: this database is a user's own list of servers and conversations, which is the kind of
/// thing that should follow them to another machine on a domain, not a local cache.
/// </para>
/// </remarks>
internal static class LocalStoreLocation
{
    /// <summary>The directory holding this app's per-user data.</summary>
    public const string DirectoryName = "TesserChat";

    /// <summary>The database's file name.</summary>
    public const string FileName = "local.db";

    /// <summary>
    /// The full path to the database, creating its directory if it does not exist.
    /// </summary>
    /// <remarks>
    /// Creates the directory but not the file — SQLite creates the file itself on first open, and a
    /// zero-byte file created here would be a valid empty database that migration then fills in,
    /// which works but makes "does this exist yet" ambiguous for anything else that asks.
    /// </remarks>
    public static string GetDatabasePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            DirectoryName);

        Directory.CreateDirectory(directory);

        return Path.Combine(directory, FileName);
    }
}
