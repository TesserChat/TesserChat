namespace TesserChat.Server.Accounts;

/// <summary>
/// Admits anyone (§5.2 mode 1).
/// </summary>
/// <remarks>
/// The default for a server that has not configured a mode. Presenting a password or an invite is
/// not an error here — a client that has one from a previous configuration is simply admitted
/// without it being read.
/// </remarks>
internal sealed class OpenAdmissionPolicy : IAdmissionPolicy
{
    /// <inheritdoc />
    public ConnectionMode Mode => ConnectionMode.Open;

    /// <inheritdoc />
    public Task<AdmissionDecision> EvaluateAsync(
        AdmissionRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AdmissionDecision.Admitted);
}
