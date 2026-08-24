using Microsoft.AspNetCore.Mvc.Testing;

namespace TesserChat.Server.Tests.Infrastructure;

/// <summary>
/// Boots the real server host with its database configuration overridden per test.
/// </summary>
/// <remarks>
/// <para>
/// The overrides are applied as environment variables rather than through
/// <c>ConfigureAppConfiguration</c>, because the server reads its connection string while building
/// the host (see <c>PersistenceExtensions.AddPersistence</c>) — before any configuration callback
/// the factory could register has run. Environment variables are also layered *after*
/// <c>appsettings.json</c>, so a developer who has a real one on disk still gets the test's values
/// rather than their own database.
/// </para>
/// <para>
/// That makes the overrides process-wide for the lifetime of the factory, which is why every test
/// that boots a host belongs to <see cref="ServerHostCollection"/> and therefore runs serially.
/// </para>
/// </remarks>
internal sealed class TesserChatServerFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringKey = "ConnectionStrings__Postgres";
    private const string MigrateOnStartupKey = "Database__MigrateOnStartup";
    private const string ConnectionModeKey = "Connection__Mode";
    private const string JoinSecretHashKey = "Connection__JoinSecretHash";
    private const string AllowlistKeyPrefix = "Connection__Allowlist__";

    private readonly List<(string Name, string? PreviousValue)> _overrides = [];

    private TesserChatServerFactory(string connectionString, bool migrateOnStartup)
    {
        Override(ConnectionStringKey, connectionString);
        Override(MigrateOnStartupKey, migrateOnStartup ? "true" : "false");
    }

    /// <summary>
    /// A host pointed at a real database, migrating on startup unless told otherwise.
    /// </summary>
    public static TesserChatServerFactory ForDatabase(string connectionString, bool migrateOnStartup = true)
        => new(connectionString, migrateOnStartup);

    /// <summary>
    /// Sets the server's connection mode and its credentials (§5.2).
    /// </summary>
    /// <remarks>
    /// Fluent so a test reads as one expression. Applied as environment variables like every other
    /// override here, and reverted on dispose.
    /// </remarks>
    public TesserChatServerFactory WithConnectionMode(
        string mode,
        string? joinSecretHash = null,
        params string[] allowlist)
    {
        Override(ConnectionModeKey, mode);

        if (joinSecretHash is not null)
        {
            Override(JoinSecretHashKey, joinSecretHash);
        }

        // Configuration binds a list from indexed keys, which is what a container operator would
        // set as Connection__Allowlist__0 as well.
        for (var i = 0; i < allowlist.Length; i++)
        {
            Override($"{AllowlistKeyPrefix}{i}", allowlist[i]);
        }

        return this;
    }

    /// <summary>
    /// A host for tests that never touch the database: migrations are off and the connection
    /// string points nowhere resolvable, so a stray query fails loudly instead of reaching a real
    /// server.
    /// </summary>
    public static TesserChatServerFactory WithoutDatabase()
        => new("Host=database.invalid;Database=tesserchat;Username=none;Password=none", migrateOnStartup: false);

    /// <summary>
    /// A host with no usable connection string, for asserting the startup failure.
    /// </summary>
    /// <remarks>
    /// Blank rather than empty: on Windows, setting an environment variable to an empty string
    /// deletes it, which would let a developer's own <c>appsettings.json</c> supply a connection
    /// string and quietly turn this into a passing configuration.
    /// </remarks>
    public static TesserChatServerFactory WithoutConnectionString()
        => new(" ", migrateOnStartup: false);

    private void Override(string name, string value)
    {
        _overrides.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var (name, previousValue) in _overrides)
            {
                Environment.SetEnvironmentVariable(name, previousValue);
            }

            _overrides.Clear();
        }

        base.Dispose(disposing);
    }
}
