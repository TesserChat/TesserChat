using System.Collections.Concurrent;

namespace TesserChat.Server.Realtime;

/// <summary>
/// Which accounts currently hold a live hub connection (§6).
/// </summary>
/// <remarks>
/// <para>
/// <b>The hub does not keep this state itself.</b> A SignalR hub instance is created per method
/// call and thrown away, so anything remembered across calls has to live somewhere else. Keeping it
/// here also keeps §6's two responsibilities separable: presence (§8.2) reads this, room chat (§17)
/// does not, and neither has to know about the other.
/// </para>
/// <para>
/// <b>An account maps to several connections, not one.</b> §8.2 has the client holding a connection
/// open to every saved server at once, and §4.4 has one identity logged in on several devices. So
/// "is this account online" is "does it hold at least one connection", and a disconnect only takes
/// an account offline when it was the last one — the distinction presence depends on being right.
/// </para>
/// <para>
/// <b>In-memory, and therefore per-instance.</b> A two-instance deployment would have each instance
/// seeing only its own connections. That is wrong for presence but not yet wrong for anything built,
/// and fixing it properly means a SignalR backplane rather than a smarter registry — noted here so
/// the limit is a known one rather than a surprise when #23 lands.
/// </para>
/// </remarks>
internal sealed class ConnectionRegistry(TimeProvider timeProvider)
{
    /// <summary>
    /// Connections by id. The authoritative record — <see cref="_byAccount"/> is an index over it.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConnectedAccount> _byConnection = new(
        StringComparer.Ordinal);

    /// <summary>
    /// Connection ids per account, so "is this account online" does not scan every connection.
    /// </summary>
    /// <remarks>
    /// Guarded by <see cref="_gate"/> rather than being concurrent itself: adding a connection has
    /// to update both dictionaries, and a reader that caught them disagreeing would report an
    /// account offline while it held a connection.
    /// </remarks>
    private readonly Dictionary<Guid, HashSet<string>> _byAccount = [];

    private readonly Lock _gate = new();

    /// <summary>
    /// Records a connection, and says whether it brought its account online.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this was the account's first connection — the transition presence
    /// announces. A second device connecting returns <see langword="false"/>: the account was
    /// already online and nothing changed for anyone watching it.
    /// </returns>
    public bool Add(string connectionId, Guid accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        var connection = new ConnectedAccount(connectionId, accountId, timeProvider.GetUtcNow());

        lock (_gate)
        {
            _byConnection[connectionId] = connection;

            if (!_byAccount.TryGetValue(accountId, out var connections))
            {
                connections = new HashSet<string>(StringComparer.Ordinal);
                _byAccount[accountId] = connections;
            }

            connections.Add(connectionId);

            return connections.Count == 1;
        }
    }

    /// <summary>
    /// Forgets a connection, and says whether it took its account offline.
    /// </summary>
    /// <remarks>
    /// Tolerates a connection it does not know. SignalR can raise a disconnect for a connection
    /// whose connect never completed — a token that failed validation, or a client that dropped
    /// mid-handshake — and that has to be a no-op rather than a throw inside the disconnect path.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> if the account has no connections left. <see langword="false"/> if it
    /// still holds another, or if this connection was not one this registry knew.
    /// </returns>
    public bool Remove(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (!_byConnection.TryRemove(connectionId, out var connection))
            {
                return false;
            }

            if (!_byAccount.TryGetValue(connection.AccountId, out var connections))
            {
                return true;
            }

            connections.Remove(connectionId);

            if (connections.Count > 0)
            {
                return false;
            }

            // The empty set is removed rather than left behind, so an account that connects and
            // disconnects repeatedly does not leave an entry per account seen since startup.
            _byAccount.Remove(connection.AccountId);
            return true;
        }
    }

    /// <summary>The account a connection authenticated as, or null if it is not connected.</summary>
    public Guid? FindAccount(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        return _byConnection.TryGetValue(connectionId, out var connection)
            ? connection.AccountId
            : null;
    }

    /// <summary>Whether an account holds at least one live connection.</summary>
    public bool IsOnline(Guid accountId)
    {
        lock (_gate)
        {
            return _byAccount.ContainsKey(accountId);
        }
    }

    /// <summary>Every live connection for an account, empty if it holds none.</summary>
    /// <remarks>
    /// A copy, not the live set: a caller iterating the registry's own collection while a
    /// disconnect mutated it would throw, and the disconnect is not something a caller controls.
    /// </remarks>
    public IReadOnlyCollection<string> ConnectionsFor(Guid accountId)
    {
        lock (_gate)
        {
            return _byAccount.TryGetValue(accountId, out var connections)
                ? [.. connections]
                : [];
        }
    }

    /// <summary>How many connections are live, across every account.</summary>
    public int ConnectionCount => _byConnection.Count;

    /// <summary>How many distinct accounts are online.</summary>
    public int OnlineAccountCount
    {
        get
        {
            lock (_gate)
            {
                return _byAccount.Count;
            }
        }
    }
}
