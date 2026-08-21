using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TesserChat.Server.Persistence;

/// <summary>
/// Registration and startup hooks for the persistence layer (§5.4).
/// </summary>
internal static class PersistenceExtensions
{
    /// <summary>
    /// Binds <see cref="DatabaseOptions"/> and registers <see cref="TesserChatDbContext"/> against
    /// the configured PostgreSQL connection string.
    /// </summary>
    /// <exception cref="InvalidOperationException">No connection string is configured.</exception>
    public static IHostApplicationBuilder AddPersistence(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<DatabaseOptions>()
            .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName));

        var connectionString = builder.Configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);

        // Fail here rather than on the first query. A server that boots without a database is not
        // usable for anything, and the operator should learn that at startup with a message naming
        // the key to set — not from a stack trace the first time someone tries to log in.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"No '{DatabaseOptions.ConnectionStringName}' connection string is configured. Set " +
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} in appsettings.json (see " +
                $"appsettings.example.json), or ConnectionStrings__{DatabaseOptions.ConnectionStringName} " +
                "as an environment variable when running in a container.");
        }

        builder.Services.AddDbContext<TesserChatDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        return builder;
    }

    /// <summary>
    /// Applies any pending migrations, unless <see cref="DatabaseOptions.MigrateOnStartup"/> is off.
    /// </summary>
    public static async Task ApplyMigrationsAsync(
        this IHost host,
        CancellationToken cancellationToken = default)
    {
        await using var scope = host.Services.CreateAsyncScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(PersistenceExtensions).FullName!);

        if (!options.MigrateOnStartup)
        {
            logger.LogInformation(
                "Skipping database migration on startup: {Section}:{Setting} is disabled.",
                DatabaseOptions.SectionName,
                nameof(DatabaseOptions.MigrateOnStartup));
            return;
        }

        var database = scope.ServiceProvider.GetRequiredService<TesserChatDbContext>().Database;

        var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date; no migrations to apply.");
            return;
        }

        logger.LogInformation(
            "Applying {Count} pending database migration(s): {Migrations}.",
            pending.Count,
            string.Join(", ", pending));

        await database.MigrateAsync(cancellationToken);
    }
}
