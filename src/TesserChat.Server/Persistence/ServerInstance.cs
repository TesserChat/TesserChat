namespace TesserChat.Server.Persistence;

/// <summary>
/// This server deployment's own identity — one row, written on first boot and never rewritten.
/// </summary>
/// <remarks>
/// <para>
/// <b>At most one row exists</b>, enforced by a single-row check constraint. Its presence is what
/// "this server has been set up" means (§5.6): the row is written by the transaction that completes
/// setup, so a server either has an identity and an Owner or has neither.
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
    /// <summary>Longest server name the column accepts.</summary>
    public const int NameMaxLength = 64;

    /// <summary>Stable identifier for this deployment, generated once on first boot.</summary>
    public Guid Id { get; init; }

    /// <summary>When this deployment first initialised its database.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The server's display name, chosen at setup and changeable afterwards by an account holding
    /// <c>server.manage</c> (§5.3).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When first-run setup completed.</summary>
    /// <remarks>
    /// The row is only ever written by the completing transaction, so this is never null for a row
    /// that exists. It is recorded rather than inferred because the audit log (§5.5) wants to say
    /// when the server came into being, and because an operator debugging a deployment wants the
    /// date without reading the Owner's role grant.
    /// </remarks>
    public DateTimeOffset SetUpAt { get; init; }

    /// <summary>The account that completed setup and was assigned Owner (§5.6).</summary>
    /// <remarks>
    /// Recorded for the audit trail. It is <i>not</i> how ownership is resolved — that is the Owner
    /// role (§5.3), which can later be granted to others or moved. This names who set the server
    /// up, which stays true even after they hand the role on.
    /// </remarks>
    public Guid SetUpByAccountId { get; init; }
}
