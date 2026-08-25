using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using TesserChat.Server.Realtime;
using TesserChat.Server.Tests.Infrastructure;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Tests.Realtime;

/// <summary>
/// The hub's connection lifecycle, over real connections (§6).
/// </summary>
[Collection(ServerHostCollection.Name)]
public sealed class TesserChatHubTests(PostgresFixture postgres)
{
    [RequiresDockerFact]
    public async Task An_unauthenticated_connection_is_refused()
    {
        await using var host = await HubHost.StartAsync(postgres);

        var connection = host.BuildConnection(token: null);

        // The handshake itself fails, so there is never a connection to call anything on.
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Equal(0, host.Registry.ConnectionCount);
    }

    [RequiresDockerFact]
    public async Task A_connection_carrying_a_forged_token_is_refused()
    {
        await using var host = await HubHost.StartAsync(postgres);

        var connection = host.BuildConnection("not.a.token");

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Equal(0, host.Registry.ConnectionCount);
    }

    [RequiresDockerFact]
    public async Task A_connection_resolves_to_the_account_that_authenticated()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);

        var connection = await host.ConnectAsync(token);

        // Asked over the connection, so the answer comes from the hub's own view of who connected.
        Assert.Equal(accountId, await connection.InvokeAsync<Guid>("WhoAmI"));
        Assert.True(host.Registry.IsOnline(accountId));
    }

    [RequiresDockerFact]
    public async Task Two_accounts_do_not_resolve_to_each_other()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var first = IdentityKeyPair.Generate();
        using var second = IdentityKeyPair.Generate();

        var (firstAccount, firstToken) = await host.RegisterAndLoginAsync(first, "First");
        var (secondAccount, secondToken) = await host.RegisterAndLoginAsync(second, "Second");

        var firstConnection = await host.ConnectAsync(firstToken);
        var secondConnection = await host.ConnectAsync(secondToken);

        Assert.Equal(firstAccount, await firstConnection.InvokeAsync<Guid>("WhoAmI"));
        Assert.Equal(secondAccount, await secondConnection.InvokeAsync<Guid>("WhoAmI"));
        Assert.NotEqual(firstAccount, secondAccount);
    }

    [RequiresDockerFact]
    public async Task Disconnecting_clears_the_connection()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);

        var connection = await host.ConnectAsync(token);
        Assert.True(host.Registry.IsOnline(accountId));

        await connection.StopAsync();

        // Presence (§8.2) is built on this being true: a client that goes away must not be left
        // showing as online.
        await WaitForAsync(() => !host.Registry.IsOnline(accountId));

        Assert.Equal(0, host.Registry.ConnectionCount);
    }

    [RequiresDockerFact]
    public async Task An_account_stays_online_while_a_second_connection_remains()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);

        // One identity on two devices (§4.4), which is the case a naive registry gets wrong.
        var first = await host.ConnectAsync(token);
        var second = await host.ConnectAsync(token);

        Assert.Equal(2, host.Registry.ConnectionsFor(accountId).Count);

        await first.StopAsync();
        await WaitForAsync(() => host.Registry.ConnectionsFor(accountId).Count == 1);

        Assert.True(host.Registry.IsOnline(accountId));

        await second.StopAsync();
        await WaitForAsync(() => !host.Registry.IsOnline(accountId));
    }

    [RequiresDockerFact]
    public async Task A_token_in_the_authorization_header_also_connects()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (accountId, token) = await host.RegisterAndLoginAsync(identity);

        var connection = await host.ConnectAsync(token, useQueryString: false);

        Assert.Equal(accountId, await connection.InvokeAsync<Guid>("WhoAmI"));
    }

    [RequiresDockerFact]
    public void The_hub_is_mapped_where_a_query_string_token_is_accepted()
    {
        // Not a connection test: a SignalR client sends its token as ?access_token= because it
        // cannot set a header on the WebSocket handshake, and §4.7.6 honours that form only under
        // "/hubs". A hub mapped anywhere else would be unreachable by a real client over WebSockets
        // — which no transport-level test catches, because long polling can fall back to a header.
        Assert.StartsWith("/hubs/", RealtimeExtensions.HubPath, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task A_query_string_token_authenticates_a_hub_request()
    {
        await using var host = await HubHost.StartAsync(postgres);

        using var identity = IdentityKeyPair.Generate();
        var (_, token) = await host.RegisterAndLoginAsync(identity);

        // Negotiate is the first request of a handshake and the one a WebSocket client can only
        // authenticate by URL. Asserted with a bare HTTP call rather than through HubConnection,
        // because the client also sends an Authorization header on the HTTP transports — which
        // would authenticate the request whether or not the query-string path worked at all.
        using var response = await host.RawClient.PostAsync(
            $"{RealtimeExtensions.HubPath}/negotiate?negotiateVersion=1&access_token={token}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task A_hub_request_without_a_token_is_refused()
    {
        await using var host = await HubHost.StartAsync(postgres);

        // The same request with the token left off, so the previous test is passing because of the
        // token rather than because the route is open.
        using var response = await host.RawClient.PostAsync(
            $"{RealtimeExtensions.HubPath}/negotiate?negotiateVersion=1",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Waits for a condition the server reaches on its own schedule.
    /// </summary>
    /// <remarks>
    /// A disconnect is observed by the server rather than reported synchronously by the client, so
    /// <c>StopAsync</c> returning does not mean the hub has run its callback yet. Polling a short
    /// window is what makes that deterministic without a fixed sleep that is either flaky or slow.
    /// </remarks>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "The expected state was not reached within the timeout.");
    }
}
