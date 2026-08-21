using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TesserChat.Server.Persistence;
using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests.Persistence;

/// <summary>
/// Covers the persistence harness end to end: the real host, its migration on startup, and a
/// round-trip through a real PostgreSQL (§5.4).
/// </summary>
[Collection(ServerHostCollection.Name)]
public sealed class PersistenceTests(PostgresFixture postgres)
{
    [RequiresDockerFact]
    public async Task Startup_AppliesMigrations_ToAnEmptyDatabase()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        using var factory = TesserChatServerFactory.ForDatabase(connectionString);
        using var _ = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TesserChatDbContext>().Database;

        Assert.NotEmpty(await database.GetAppliedMigrationsAsync());
        Assert.Empty(await database.GetPendingMigrationsAsync());
    }

    [RequiresDockerFact]
    public async Task Startup_LeavesTheDatabaseUntouched_WhenMigrateOnStartupIsOff()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        using var factory = TesserChatServerFactory.ForDatabase(connectionString, migrateOnStartup: false);
        using var _ = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TesserChatDbContext>().Database;

        Assert.Empty(await database.GetAppliedMigrationsAsync());
    }

    [RequiresDockerFact]
    public async Task ServerInstance_RoundTripsThroughPostgres()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        using var factory = TesserChatServerFactory.ForDatabase(connectionString);
        using var _ = factory.CreateClient();

        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        await using (var writeScope = factory.Services.CreateAsyncScope())
        {
            var context = writeScope.ServiceProvider.GetRequiredService<TesserChatDbContext>();
            context.ServerInstances.Add(new ServerInstance { Id = id, CreatedAt = createdAt });
            await context.SaveChangesAsync();
        }

        // A second scope means a second DbContext, so this reads Postgres rather than the change
        // tracker that wrote it.
        await using (var readScope = factory.Services.CreateAsyncScope())
        {
            var context = readScope.ServiceProvider.GetRequiredService<TesserChatDbContext>();
            var stored = await context.ServerInstances.SingleAsync(instance => instance.Id == id);

            Assert.Equal(id, stored.Id);

            // timestamptz resolves to microseconds, so the .NET value's finer ticks are rounded on
            // the way in. Anything larger than that is a mapping bug, not precision.
            Assert.True(
                (stored.CreatedAt - createdAt).Duration() < TimeSpan.FromMilliseconds(1),
                $"Round-tripped timestamp {stored.CreatedAt:O} differs from {createdAt:O}.");
        }
    }

    [RequiresDockerFact]
    public async Task Health_StillAnswers_OnAMigratedHost()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        using var factory = TesserChatServerFactory.ForDatabase(connectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
    }
}
