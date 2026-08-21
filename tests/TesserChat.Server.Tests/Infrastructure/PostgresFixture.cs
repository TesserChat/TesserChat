using Npgsql;
using Testcontainers.PostgreSql;

namespace TesserChat.Server.Tests.Infrastructure;

/// <summary>
/// One throwaway PostgreSQL container, shared by every test in <see cref="ServerHostCollection"/>.
/// </summary>
/// <remarks>
/// A real Postgres rather than the in-memory provider, per §5.4: the in-memory provider does not
/// enforce constraints or model Postgres behaviour, so it would happily accept a schema Postgres
/// rejects. Starting a container costs a few seconds once per run, which is why it is a collection
/// fixture and why tests isolate themselves with <see cref="CreateDatabaseAsync"/> instead of
/// starting a container each.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Pinned rather than <c>latest</c>, so a new Postgres release cannot change what CI tests
    /// without a commit saying so.
    /// </summary>
    private const string PostgresImage = "postgres:18-alpine";

    private PostgreSqlContainer? _container;

    /// <remarks>
    /// The container is built here rather than in a field initialiser because xUnit constructs and
    /// disposes the fixture even when every test in the collection is skipped — and both building
    /// and disposing one reaches for a Docker daemon. A fixture that throws fails the whole
    /// collection, which would turn "Docker is not available" into seven failures instead of seven
    /// skips.
    /// </remarks>
    public async Task InitializeAsync()
    {
        if (DockerAvailability.SkipReason is not null)
        {
            return;
        }

        _container = new PostgreSqlBuilder(PostgresImage).Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates an empty database on the shared container and returns a connection string for it.
    /// </summary>
    /// <remarks>
    /// Per-test databases keep migration state from leaking between tests — a test asserting that
    /// nothing was migrated cannot share a database with one that migrates.
    /// </remarks>
    public async Task<string> CreateDatabaseAsync()
    {
        if (_container is null)
        {
            throw new InvalidOperationException(
                "The Postgres container is not running. A test that needs it must be marked "
                + $"[{nameof(RequiresDockerFactAttribute)}].");
        }

        // Hex from a GUID, so the name is always a safe bare identifier — CREATE DATABASE cannot
        // take a parameter, and this way nothing external ever reaches the statement text.
        var databaseName = $"tesserchat_test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName,
        }.ConnectionString;
    }
}
