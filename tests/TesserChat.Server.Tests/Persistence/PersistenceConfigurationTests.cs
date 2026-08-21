using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests.Persistence;

/// <summary>
/// Configuration-time behaviour of the persistence layer — no database involved, so these run on
/// every platform in the CI matrix rather than only where Docker can serve Linux containers.
/// </summary>
[Collection(ServerHostCollection.Name)]
public sealed class PersistenceConfigurationTests
{
    [Fact]
    public void Startup_Fails_WhenNoConnectionStringIsConfigured()
    {
        using var factory = TesserChatServerFactory.WithoutConnectionString();

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);

        // Named precisely, because this message is what an operator has to act on. ToString()
        // rather than Message: the host wraps startup failures.
        Assert.Contains("ConnectionStrings:Postgres", exception.ToString(), StringComparison.Ordinal);
    }
}
