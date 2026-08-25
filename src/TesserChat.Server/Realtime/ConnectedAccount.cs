namespace TesserChat.Server.Realtime;

/// <summary>
/// One live hub connection, and the account it authenticated as (§6).
/// </summary>
/// <param name="ConnectionId">SignalR's id for this connection.</param>
/// <param name="AccountId">
/// The account the connection's token proved. Taken from the validated principal, never from
/// anything the client sends over the connection afterwards.
/// </param>
/// <param name="ConnectedAt">When the connection was established.</param>
internal readonly record struct ConnectedAccount(
    string ConnectionId,
    Guid AccountId,
    DateTimeOffset ConnectedAt);
