namespace TesserChat.Server.Persistence;

/// <summary>
/// This server deployment's own identity — one row, written on first boot and never rewritten.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately minimal. Server-operator configuration (name, connection mode, the Owner
/// assignment) belongs to the first-run setup flow in §5.6 and extends this row there; the schema
/// here exists so the persistence layer has something real to migrate and round-trip.
/// </para>
/// <para>
/// The id is not derived from anything — unlike an account id (§5.1), a server has no keypair to
/// derive from. It is a random UUID whose only job is to stay stable for the life of the
/// deployment, so that the login nonce scoping in §4.7 has a server identity to bind a signature
/// to.
/// </para>
/// </remarks>
internal sealed class ServerInstance
{
    /// <summary>Stable identifier for this deployment, generated once on first boot.</summary>
    public Guid Id { get; init; }

    /// <summary>When this deployment first initialised its database.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
