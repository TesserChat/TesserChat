using TesserChat.Server.Realtime;

namespace TesserChat.Server.Tests.Realtime;

/// <summary>
/// The bookkeeping presence (§8.2) is built on, tested without a host (§6).
/// </summary>
/// <remarks>
/// The transitions matter more than the counts: what presence announces is an account coming online
/// and going offline, not every connection opening and closing.
/// </remarks>
public sealed class ConnectionRegistryTests
{
    /// <remarks>
    /// The real clock: the registry reads it only to stamp when a connection opened, and nothing
    /// here asserts on that stamp. A fake would be a dependency bought for nothing.
    /// </remarks>
    private static ConnectionRegistry Create() => new(TimeProvider.System);

    [Fact]
    public void A_first_connection_brings_an_account_online()
    {
        var registry = Create();
        var account = Guid.NewGuid();

        Assert.True(registry.Add("connection-1", account));
        Assert.True(registry.IsOnline(account));
    }

    [Fact]
    public void A_second_connection_does_not_announce_a_change()
    {
        var registry = Create();
        var account = Guid.NewGuid();

        registry.Add("connection-1", account);

        // Already online, so nothing changed for anyone watching this account.
        Assert.False(registry.Add("connection-2", account));
        Assert.Equal(1, registry.OnlineAccountCount);
        Assert.Equal(2, registry.ConnectionCount);
    }

    [Fact]
    public void Only_the_last_disconnect_takes_an_account_offline()
    {
        var registry = Create();
        var account = Guid.NewGuid();

        registry.Add("connection-1", account);
        registry.Add("connection-2", account);

        Assert.False(registry.Remove("connection-1"));
        Assert.True(registry.IsOnline(account));

        Assert.True(registry.Remove("connection-2"));
        Assert.False(registry.IsOnline(account));
    }

    [Fact]
    public void Removing_an_unknown_connection_is_a_no_op()
    {
        var registry = Create();

        // SignalR can raise a disconnect for a connection whose connect never completed.
        Assert.False(registry.Remove("never-connected"));
        Assert.Equal(0, registry.ConnectionCount);
    }

    [Fact]
    public void Removing_the_same_connection_twice_does_not_double_count()
    {
        var registry = Create();
        var account = Guid.NewGuid();

        registry.Add("connection-1", account);

        Assert.True(registry.Remove("connection-1"));
        Assert.False(registry.Remove("connection-1"));
        Assert.False(registry.IsOnline(account));
    }

    [Fact]
    public void A_connection_resolves_to_its_own_account()
    {
        var registry = Create();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        registry.Add("connection-1", first);
        registry.Add("connection-2", second);

        Assert.Equal(first, registry.FindAccount("connection-1"));
        Assert.Equal(second, registry.FindAccount("connection-2"));
        Assert.Null(registry.FindAccount("connection-3"));
    }

    [Fact]
    public void One_account_going_offline_leaves_another_online()
    {
        var registry = Create();
        var staying = Guid.NewGuid();
        var leaving = Guid.NewGuid();

        registry.Add("connection-1", staying);
        registry.Add("connection-2", leaving);

        Assert.True(registry.Remove("connection-2"));

        Assert.True(registry.IsOnline(staying));
        Assert.False(registry.IsOnline(leaving));
        Assert.Equal(1, registry.OnlineAccountCount);
    }

    [Fact]
    public void An_offline_account_has_no_connections()
    {
        var registry = Create();

        Assert.Empty(registry.ConnectionsFor(Guid.NewGuid()));
    }

    [Fact]
    public void Connections_for_an_account_are_a_snapshot()
    {
        var registry = Create();
        var account = Guid.NewGuid();

        registry.Add("connection-1", account);
        var snapshot = registry.ConnectionsFor(account);

        // Mutating the registry afterwards must not disturb a collection a caller is iterating.
        registry.Add("connection-2", account);

        Assert.Single(snapshot);
        Assert.Equal(2, registry.ConnectionsFor(account).Count);
    }

    [Fact]
    public void Concurrent_connects_and_disconnects_settle_correctly()
    {
        var registry = Create();
        var account = Guid.NewGuid();

        // The registry updates two dictionaries per call, so a reader catching them mid-update is
        // the failure this guards against.
        Parallel.For(0, 200, i => registry.Add($"connection-{i}", account));

        Assert.Equal(200, registry.ConnectionCount);
        Assert.True(registry.IsOnline(account));

        Parallel.For(0, 200, i => registry.Remove($"connection-{i}"));

        Assert.Equal(0, registry.ConnectionCount);
        Assert.False(registry.IsOnline(account));
        Assert.Equal(0, registry.OnlineAccountCount);
    }

    [Fact]
    public void Exactly_one_concurrent_disconnect_reports_the_account_offline()
    {
        var registry = Create();
        var account = Guid.NewGuid();

        for (var i = 0; i < 50; i++)
        {
            registry.Add($"connection-{i}", account);
        }

        var offlineReports = 0;

        Parallel.For(0, 50, i =>
        {
            if (registry.Remove($"connection-{i}"))
            {
                Interlocked.Increment(ref offlineReports);
            }
        });

        // Presence announces an account going offline once, not once per racing disconnect.
        Assert.Equal(1, offlineReports);
    }
}
