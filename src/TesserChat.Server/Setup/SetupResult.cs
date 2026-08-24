namespace TesserChat.Server.Setup;

/// <summary>
/// Why a setup attempt was refused, or that it succeeded.
/// </summary>
internal enum SetupStatus
{
    /// <summary>Setup completed: the server has an identity, a name, and one Owner.</summary>
    Completed,

    /// <summary>
    /// This server has already been set up.
    /// </summary>
    /// <remarks>
    /// <b>The security-relevant refusal (§5.6).</b> Setup is unauthenticated by necessity — there is
    /// no Owner yet to authorize it — so an already-configured server must refuse it outright.
    /// Otherwise re-running setup would be a way to seize Owner on a live server, from an
    /// unauthenticated request, at any time.
    /// </remarks>
    AlreadyConfigured,

    /// <summary>
    /// A public key was pinned in configuration and the caller presented a different one.
    /// </summary>
    /// <remarks>
    /// Only reachable while the server is unconfigured. Pinning turns setup from a race into a
    /// claim that exactly one key can make, which is what makes exposing a fresh server safe.
    /// </remarks>
    NotThePinnedOwner,

    /// <summary>The server name was blank after trimming, or too long.</summary>
    InvalidServerName,

    /// <summary>The display name for the Owner's own account was not acceptable.</summary>
    InvalidDisplayName,
}

/// <summary>
/// The outcome of a setup attempt.
/// </summary>
/// <param name="Status">Whether setup completed, and why not if it did not.</param>
/// <param name="ServerId">The server's stable id, when setup completed.</param>
/// <param name="OwnerAccountId">The account assigned Owner, when setup completed.</param>
internal readonly record struct SetupResult(
    SetupStatus Status,
    Guid ServerId,
    Guid OwnerAccountId)
{
    /// <summary>Whether setup completed.</summary>
    public bool Succeeded => Status == SetupStatus.Completed;

    internal static SetupResult Completed(Guid serverId, Guid ownerAccountId)
        => new(SetupStatus.Completed, serverId, ownerAccountId);

    internal static SetupResult Refused(SetupStatus status) => new(status, Guid.Empty, Guid.Empty);
}
