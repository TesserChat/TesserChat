namespace TesserChat.Server.Persistence;

/// <summary>
/// A single-use login challenge this server issued and is waiting to see signed (§4.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>In the database rather than in memory, so single-use is a fact rather than a hope.</b>
/// Consuming a nonce is a conditional UPDATE, which Postgres serialises: two simultaneous
/// presentations of the same nonce cannot both find it unconsumed, so the replay loses even when it
/// arrives at the same instant as the original. An in-process cache would give the same answer
/// right up until a self-hoster ran a second instance, at which point it would quietly stop being
/// true.
/// </para>
/// <para>
/// Rows are kept after being consumed rather than deleted. A consumed row is what makes a replay
/// distinguishable from a nonce that never existed, and it keeps the table honest for the sweep
/// that eventually clears both.
/// </para>
/// <para>
/// <b>This is not an audit record.</b> Nonces are issued to anyone who asks, before any identity is
/// proven, so the table says nothing about who logged in — only that a challenge was handed out.
/// The audit log (§5.5) is where authenticated actions are recorded.
/// </para>
/// </remarks>
internal sealed class LoginNonce
{
    /// <summary>The random challenge bytes, which are also the primary key.</summary>
    /// <remarks>
    /// The value is the identity: a surrogate key would let the same bytes be inserted twice, which
    /// is the one thing this table exists to prevent.
    /// </remarks>
    public byte[] Value { get; init; } = [];

    /// <summary>When this nonce was handed out.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// When it stops being usable, whether or not anyone consumed it.
    /// </summary>
    /// <remarks>
    /// Stored rather than computed from <see cref="IssuedAt"/> plus the configured lifetime, so an
    /// operator shortening the lifetime cannot retroactively expire a challenge a client is in the
    /// middle of signing.
    /// </remarks>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// When it was spent, or <see langword="null"/> while it is still outstanding.
    /// </summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}
