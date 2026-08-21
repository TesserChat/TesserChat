namespace TesserChat.Server.Persistence;

/// <summary>
/// The <c>Database</c> configuration section (§5.4).
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Name of the connection string under <c>ConnectionStrings</c>. In a container this is
    /// overridable as the <c>ConnectionStrings__Postgres</c> environment variable.
    /// </summary>
    public const string ConnectionStringName = "Postgres";

    /// <summary>
    /// Whether pending migrations are applied when the server starts. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// On by default because the target deployment is a self-hosted operator pulling a new Docker
    /// image (§5.6): an upgrade that silently boots against last version's schema is a worse
    /// failure than one that applies its own migrations. Operators running a managed or
    /// change-controlled database can turn this off and apply migrations themselves, in which case
    /// a server started against an out-of-date schema will fail on first use rather than at boot.
    /// </remarks>
    public bool MigrateOnStartup { get; set; } = true;
}
