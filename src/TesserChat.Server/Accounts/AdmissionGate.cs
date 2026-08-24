using Microsoft.Extensions.Options;

namespace TesserChat.Server.Accounts;

/// <summary>
/// Applies the server's configured admission path to a registration attempt (§5.2).
/// </summary>
/// <remarks>
/// <para>
/// Selects the <see cref="IAdmissionPolicy"/> matching the configured
/// <see cref="ConnectionOptions.Mode"/> and asks it. Selection happens per call through
/// <see cref="IOptionsMonitor{TOptions}"/>, so an Owner changing the mode takes effect on the next
/// registration rather than at the next restart.
/// </para>
/// <para>
/// When invites land (#44) they are consulted here as an alternative path — a valid invite admits a
/// key the mode alone would refuse — rather than becoming a fourth mode.
/// </para>
/// </remarks>
internal sealed class AdmissionGate(
    IEnumerable<IAdmissionPolicy> policies,
    IOptionsMonitor<ConnectionOptions> options,
    ILogger<AdmissionGate> logger)
{
    private readonly IReadOnlyDictionary<ConnectionMode, IAdmissionPolicy> _policies =
        policies.ToDictionary(policy => policy.Mode);

    /// <summary>
    /// Whether <paramref name="request"/> may register on this server.
    /// </summary>
    public async Task<AdmissionDecision> EvaluateAsync(
        AdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        var mode = options.CurrentValue.Mode;

        if (!_policies.TryGetValue(mode, out var policy))
        {
            // Only reachable if a mode is added to the enum without a policy registered for it.
            // Refusing is the safe direction: an unrecognised mode must not mean "let everyone in".
            logger.LogError(
                "No admission policy is registered for connection mode {Mode}; refusing all "
                + "registrations.",
                mode);

            return AdmissionDecision.Refused;
        }

        return await policy.EvaluateAsync(request, cancellationToken);
    }
}
