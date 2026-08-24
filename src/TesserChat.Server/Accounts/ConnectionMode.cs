namespace TesserChat.Server.Accounts;

/// <summary>
/// How a server admits new members (§5.2).
/// </summary>
/// <remarks>
/// <para>
/// Set at first-run setup (§5.6) and changeable afterwards by an account holding
/// <c>server.manage</c> (§5.3).
/// </para>
/// <para>
/// The mode governs <b>registration only</b>, never login. Once a key is registered it authenticates
/// by challenge-response (§4.7) and never presents a joining credential again — which is what makes
/// <see cref="PasswordGated"/> a first-contact gate rather than a password on the server.
/// </para>
/// <para>
/// This is not the complete set of ways a key can be admitted: an invite (#44) admits a key that the
/// mode alone would refuse. Modes and invites are separate admission paths, not variations of each
/// other.
/// </para>
/// </remarks>
internal enum ConnectionMode
{
    /// <summary>Anyone may register. The default for a server that has said nothing.</summary>
    /// <remarks>
    /// First in the enum so that the zero value — what an unset configuration binds to — is the
    /// documented default rather than an accident. §5.2 lists Open first for the same reason: it is
    /// what a community server that has not thought about it wants.
    /// </remarks>
    Open = 0,

    /// <summary>
    /// A shared password is required for the first registration; later logins are not affected.
    /// </summary>
    PasswordGated = 1,

    /// <summary>Only public keys pre-approved by the operator may register.</summary>
    AllowlistOnly = 2,
}
