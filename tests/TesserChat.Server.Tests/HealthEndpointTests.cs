using System.Net;
using System.Net.Http.Json;
using TesserChat.Server.Tests.Infrastructure;

namespace TesserChat.Server.Tests;

/// <summary>
/// Boots the real server host in-memory. Doubles as the check that the whole
/// Server → Shared reference chain actually starts up, not just compiles.
/// </summary>
/// <remarks>
/// Uses a host with no database (§5.4): the probe is meant to answer whether the process is alive,
/// so it must not need Postgres to do it — that is what keeps it usable as a container liveness
/// check while the database is still starting.
/// </remarks>
[Collection(ServerHostCollection.Name)]
public sealed class HealthEndpointTests : IDisposable
{
    private readonly TesserChatServerFactory _factory = TesserChatServerFactory.WithoutDatabase();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsStatusOk()
    {
        var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
    }

    private sealed record HealthResponse(string Status);
}
