namespace TesserChat.Server.Accounts;

/// <summary>
/// Decides whether a prospective member may register (§5.2).
/// </summary>
/// <remarks>
/// <para>
/// One implementation per admission path. The three connection modes are the paths that exist
/// today; an invite (#44) becomes another implementation rather than a fourth mode, which is why
/// this is an interface over a request object instead of a switch over
/// <see cref="ConnectionMode"/>.
/// </para>
/// <para>
/// A policy answers only "may this key register". It does not create the account, does not decide
/// what roles the new member gets, and is never consulted at login — registration is the only
/// moment a joining credential is presented (§4.7).
/// </para>
/// </remarks>
internal interface IAdmissionPolicy
{
    /// <summary>The mode this policy implements.</summary>
    ConnectionMode Mode { get; }

    /// <summary>
    /// Whether <paramref name="request"/> may register.
    /// </summary>
    /// <remarks>
    /// Implementations must not disclose <i>why</i> a request was refused beyond
    /// <see cref="AdmissionDecision.Refused"/> — see <see cref="AdmissionDecision"/>.
    /// </remarks>
    Task<AdmissionDecision> EvaluateAsync(
        AdmissionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A policy's answer: admitted, or refused.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately two-valued. A refusal carries no reason, because every distinguishable reason is
/// something a stranger can learn about a server they have not joined:
/// </para>
/// <list type="bullet">
/// <item>
/// "wrong password" versus "not on the allowlist" tells an unauthenticated caller which mode the
/// server runs, which the issue for §5.2 calls out explicitly.
/// </item>
/// <item>
/// "you are not on the allowlist" is a membership oracle of the kind §7.4.1 and §8.2 refuse
/// elsewhere — ask about enough keys and the allowlist is enumerable.
/// </item>
/// </list>
/// <para>
/// The operator already knows their own mode and tells invitees out of band. Nobody who needs the
/// distinction has to learn it from a rejection.
/// </para>
/// </remarks>
internal enum AdmissionDecision
{
    /// <summary>The key may register.</summary>
    Admitted,

    /// <summary>The key may not register. No reason is given, by design.</summary>
    Refused,
}
