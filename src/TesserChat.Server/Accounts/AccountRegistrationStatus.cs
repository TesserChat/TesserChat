namespace TesserChat.Server.Accounts;

/// <summary>
/// Why a registration attempt was rejected, or that it succeeded.
/// </summary>
/// <remarks>
/// Registration is driven by input from an unauthenticated caller, so rejection is an expected
/// outcome rather than an exceptional one. Returning a status keeps the ordinary rejection paths
/// out of exception handling; genuinely exceptional conditions (the database being unreachable)
/// still throw.
/// </remarks>
internal enum AccountRegistrationStatus
{
    /// <summary>The account now exists, whether newly created or already present.</summary>
    Registered,

    /// <summary>A public key was missing, the wrong length, or not a usable key.</summary>
    InvalidPublicKey,

    /// <summary>The display name was empty, whitespace, or too long.</summary>
    InvalidDisplayName,

    /// <summary>
    /// The server's connection mode does not admit this key (§5.2).
    /// </summary>
    /// <remarks>
    /// Carries no detail about <i>why</i>, deliberately — see <see cref="AdmissionDecision"/>. A
    /// caller cannot tell a wrong joining password from an unlisted key, which is what stops an
    /// unauthenticated stranger from learning a server's mode or probing its allowlist.
    /// </remarks>
    NotPermitted,
}
