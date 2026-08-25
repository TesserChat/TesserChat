namespace TesserChat.Server.Auth;

/// <summary>
/// Why a login attempt was refused, or that it succeeded.
/// </summary>
/// <remarks>
/// <para>
/// Login is driven entirely by unauthenticated input, so refusal is the ordinary case rather than
/// an exceptional one and is reported as a status instead of an exception.
/// </para>
/// <para>
/// <b>These distinctions are for the server's own logs, not for the caller.</b> The endpoint that
/// eventually surfaces this (#13) should answer a failed login with one undifferentiated
/// rejection. Telling a stranger that their nonce expired rather than that their key is unknown
/// turns login into an oracle for which public keys are registered here, which is the same
/// enumeration boundary §5.2 draws around admission and §8.2 draws around presence.
/// </para>
/// </remarks>
internal enum LoginStatus
{
    /// <summary>The signature verified against a registered key over an unspent challenge.</summary>
    Authenticated,

    /// <summary>
    /// The nonce is not one this server issued, or it has already been spent.
    /// </summary>
    /// <remarks>
    /// Unknown and already-spent are one status on purpose. Separating them would confirm to an
    /// attacker holding a captured nonce that it had been genuine, which is the only thing a replay
    /// attempt could otherwise learn.
    /// </remarks>
    UnknownOrSpentChallenge,

    /// <summary>The nonce was issued by this server but its lifetime has run out.</summary>
    ExpiredChallenge,

    /// <summary>The signature does not verify for the presented key over this challenge.</summary>
    /// <remarks>
    /// Also the outcome for a signature made for a <i>different</i> server: the target server's id
    /// is part of the signed payload, so a replayed signature verifies over different bytes and
    /// simply fails. That is the property §4.7 exists to provide.
    /// </remarks>
    InvalidSignature,

    /// <summary>The presented public key is not registered on this server.</summary>
    UnknownAccount,

    /// <summary>The server has not completed first-run setup, so it has no identity to bind to.</summary>
    /// <remarks>
    /// The signed payload includes this server's id (§4.7), which does not exist until setup writes
    /// it. An unconfigured server therefore cannot authenticate anyone — the way in is to complete
    /// setup (§5.6), not to log in.
    /// </remarks>
    ServerNotConfigured,
}

/// <summary>
/// The outcome of a challenge-response login attempt.
/// </summary>
/// <param name="Status">Whether the login succeeded, and why not if it did not.</param>
/// <param name="AccountId">The authenticated account, when the login succeeded.</param>
internal readonly record struct LoginResult(LoginStatus Status, Guid AccountId)
{
    /// <summary>Whether the caller proved possession of a registered identity's private key.</summary>
    public bool Succeeded => Status == LoginStatus.Authenticated;

    internal static LoginResult Authenticated(Guid accountId)
        => new(LoginStatus.Authenticated, accountId);

    internal static LoginResult Refused(LoginStatus status) => new(status, Guid.Empty);
}
