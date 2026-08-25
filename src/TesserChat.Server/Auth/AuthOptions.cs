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

    /// <summary>
    /// How long a session token is accepted after being issued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twelve hours, and <b>there is no refresh token</b> — re-running challenge-response is the
    /// refresh path (§4.7.6). That is what sets this value: expiry costs a client one round trip
    /// against a key it already holds, so the lifetime can be short without the usual penalty of
    /// making people log in again.
    /// </para>
    /// <para>
    /// <b>Bounded because nothing else bounds it.</b> Tokens are stateless — the server does not
    /// record which it has issued, so it cannot revoke one — which means expiry is the only thing
    /// that ends a stolen token's usefulness. Long enough to cover a working day without a
    /// re-authentication mid-conversation; short enough that a token lifted from a client's storage
    /// is not a permanent key to that account.
    /// </para>
    /// <para>
    /// Raising this on a self-hosted server is a real tradeoff rather than a preference: it extends
    /// exactly that window. Lowering it is close to free, since the client re-authenticates
    /// silently.
    /// </para>
    /// </remarks>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Tolerance allowed when checking a token's expiry against this server's clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thirty seconds, against a library default of five minutes. The default exists for
    /// federated deployments where issuer and validator are different machines whose clocks drift;
    /// here they are the same process, so the only skew that matters is between the server and a
    /// client deciding when to re-authenticate. Five minutes of grace on a twelve-hour token is
    /// five extra minutes a stolen token keeps working, bought for nothing.
    /// </para>
    /// <para>
    /// Not zero, because a token is checked against a clock that may have ticked since it was
    /// issued, and rejecting a token the same process signed moments ago would be its own bug.
    /// </para>
    /// </remarks>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}
