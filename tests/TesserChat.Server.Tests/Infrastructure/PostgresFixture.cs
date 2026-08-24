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

    /// <summary>
    /// Connections each booted host may hold open.
    /// </summary>
    /// <remarks>
    /// Postgres allows 100 connections by default and Npgsql's own default pool is 100, so a single
    /// host could exhaust the server on its own. Tests boot a host per case and several boot two at
    /// once, which is how CI hit "sorry, too many clients already" on a run that passed locally —
    /// the ceiling is reached by however many hosts happen to overlap, so the test that reports it
    /// is arbitrary.
    /// </remarks>
    private const int MaxPoolSizePerHost = 5;

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

        _container = new PostgreSqlBuilder(PostgresImage)
            // Above the 100 Postgres allows by default. Each booted host holds a bounded pool
            // (MaxPoolSizePerHost), but pools are keyed by connection string and linger briefly
            // after a host is disposed, so overlapping hosts can still stack up on a slow runner.
            // Headroom here is free; a flaky "too many clients" failure is not.
            .WithCommand("-c", "max_connections=300")
            .Build();

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
            MaxPoolSize = MaxPoolSizePerHost,
        }.ConnectionString;
    }
}
