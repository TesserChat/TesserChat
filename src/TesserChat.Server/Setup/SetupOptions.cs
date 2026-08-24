namespace TesserChat.Server.Setup;

/// <summary>
/// First-run setup configuration (§5.6).
/// </summary>
/// <remarks>
/// Bound from the <c>Setup</c> section, so an operator can pin these in <c>appsettings.json</c> or
/// as <c>Setup__OwnerPublicKey</c> in a container (§3.2). Every value here is read only while the
/// server is unconfigured; once setup completes, changing them does nothing.
/// </remarks>
internal sealed class SetupOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Setup";

    /// <summary>
    /// The public key permitted to claim Owner, pinned before first boot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Base64url — either a bare Ed25519 signing key or the full shareable identity token, matching
    /// what the allowlist accepts (§5.2). When set, <b>only</b> this key can complete setup, so
    /// exposing a fresh server to the internet is safe: setup stops being a race and becomes a
    /// claim that one key alone can make.
    /// </para>
    /// <para>
    /// When unset, the first client to complete setup becomes Owner. That is the right default for
    /// someone bringing a server up on a machine that is not yet reachable from outside, and the
    /// wrong one for a container published straight to a public address — so leaving it unset logs
    /// a warning on every boot until setup completes.
    /// </para>
    /// </remarks>
    public string? OwnerPublicKey { get; set; }

    /// <summary>
    /// The server's display name, shown to members and in the client's server rail.
    /// </summary>
    /// <remarks>
    /// Optional. Setup falls back to a placeholder if it is neither configured nor supplied by the
    /// client completing setup, so a server is never nameless.
    /// </remarks>
    public string? ServerName { get; set; }
}
