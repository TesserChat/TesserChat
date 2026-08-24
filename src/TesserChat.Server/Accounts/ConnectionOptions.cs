namespace TesserChat.Server.Accounts;

/// <summary>
/// How this server admits new members, as configured (§5.2).
/// </summary>
/// <remarks>
/// <para>
/// Bound from the <c>Connection</c> configuration section, so an operator can set it in
/// <c>appsettings.json</c> or override it with <c>Connection__Mode</c> in a container (§3.2).
/// First-run setup (§5.6) writes it for an operator who would rather answer a wizard.
/// </para>
/// <para>
/// Read through <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> rather than
/// <c>IOptions</c>, so an Owner changing the mode takes effect without a restart.
/// </para>
/// </remarks>
internal sealed class ConnectionOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Connection";

    /// <summary>
    /// How new members are admitted. Defaults to <see cref="ConnectionMode.Open"/>.
    /// </summary>
    public ConnectionMode Mode { get; set; } = ConnectionMode.Open;

    /// <summary>
    /// The hashed shared joining password, when <see cref="Mode"/> is
    /// <see cref="ConnectionMode.PasswordGated"/>.
    /// </summary>
    /// <remarks>
    /// <b>A hash, never the password itself</b> — produced by <see cref="JoinSecretHasher.Hash"/>.
    /// A server configured for password-gated joining with this unset admits nobody, which is the
    /// safe direction to fail: a misconfiguration that turns a gated server open would hand the
    /// server to whoever noticed first.
    /// </remarks>
    public string? JoinSecretHash { get; set; }

    /// <summary>
    /// Public keys permitted to register when <see cref="Mode"/> is
    /// <see cref="ConnectionMode.AllowlistOnly"/>.
    /// </summary>
    /// <remarks>
    /// Base64url-encoded raw Ed25519 public keys, matching the shareable form a prospective member
    /// can send the operator (<c>PublicIdentity.ToShareableString</c> carries both keys; the
    /// signing key alone is what is listed here). Entries that do not parse are ignored rather than
    /// failing startup — one typo in a long list should not take a server offline — and the
    /// allowlist policy logs how many were skipped.
    /// </remarks>
    public IList<string> Allowlist { get; } = [];
}
