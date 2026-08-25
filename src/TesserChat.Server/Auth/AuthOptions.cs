namespace TesserChat.Server.Auth;

/// <summary>
/// Configuration for challenge-response login (§4.7), bound from the <c>Auth</c> section.
/// </summary>
internal sealed class AuthOptions
{
    /// <summary>Configuration section these settings bind from.</summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// How long a client has to sign a challenge after asking for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two minutes: long enough to cover a slow link and a user's device waking up, short enough
    /// that a challenge captured off the wire is worthless by the time it could be used. Single-use
    /// is what actually prevents replay — this bounds how long an unspent nonce stays interesting,
    /// and how large the table grows between sweeps.
    /// </para>
    /// <para>
    /// Signing is a local operation taking microseconds, so this is almost entirely network and
    /// user-interface latency. It is configurable for the self-hoster on a genuinely bad link, not
    /// because the default needs tuning.
    /// </para>
    /// </remarks>
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long spent and expired challenges are kept before the sweep removes them.
    /// </summary>
    /// <remarks>
    /// Not zero, so that a replay arriving just after its nonce expired still meets a row and is
    /// refused as spent rather than silently becoming an unknown nonce. Beyond that the rows have
    /// no value — they are not an audit trail (see <c>LoginNonce</c>) — so this only needs to
    /// exceed the clock skew between a client and the server.
    /// </remarks>
    public TimeSpan ChallengeRetention { get; set; } = TimeSpan.FromMinutes(15);
}
