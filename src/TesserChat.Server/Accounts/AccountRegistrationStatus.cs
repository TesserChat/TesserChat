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
    /// Nothing produces this yet — connection modes are #9. It exists so the gate has a status to
    /// return when it lands, rather than that change having to widen this enum and every switch
    /// over it.
    /// </remarks>
    NotPermitted,
}
