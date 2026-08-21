namespace TesserChat.Server.Tests.Infrastructure;

/// <summary>
/// Groups every test that boots a server host, so they run serially against one shared Postgres
/// container.
/// </summary>
/// <remarks>
/// Serial execution is required, not just convenient: <see cref="TesserChatServerFactory"/>
/// overrides configuration through process-wide environment variables, so two hosts booting
/// concurrently would read each other's settings.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ServerHostCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Server host";
}
