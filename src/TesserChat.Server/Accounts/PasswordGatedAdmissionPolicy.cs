using Microsoft.Extensions.Options;

namespace TesserChat.Server.Accounts;

/// <summary>
/// Admits a key that presents the server's shared joining password (§5.2 mode 2).
/// </summary>
/// <remarks>
/// <para>
/// The password gates <b>registration only</b>. Once a key is registered it authenticates by
/// challenge-response (§4.7) and never presents this again, so rotating it locks out prospective
/// members without disturbing existing ones. That is the whole point of the mode: a password shared
/// with a community is a joining ritual, not an ongoing credential.
/// </para>
/// <para>
/// Since one secret is shared by everyone, it cannot be revoked for one person and it leaks the
/// first time someone forwards it. Invites (#44) exist to fix exactly that, and are a separate
/// admission path rather than a change to this one.
/// </para>
/// </remarks>
internal sealed class PasswordGatedAdmissionPolicy(
    IOptionsMonitor<ConnectionOptions> options,
    ILogger<PasswordGatedAdmissionPolicy> logger) : IAdmissionPolicy
{
    /// <inheritdoc />
    public ConnectionMode Mode => ConnectionMode.PasswordGated;

    /// <inheritdoc />
    public Task<AdmissionDecision> EvaluateAsync(
        AdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var configured = options.CurrentValue.JoinSecretHash;

        if (string.IsNullOrWhiteSpace(configured))
        {
            // Fail closed. A password-gated server with no password configured admits nobody rather
            // than everybody — the alternative silently turns a restricted server open, which is
            // the misconfiguration that actually costs something.
            logger.LogError(
                "Connection mode is {Mode} but no {Section}:{Setting} is configured; refusing all "
                + "registrations. Set it to a value produced by hashing the joining password, or "
                + "change the mode.",
                ConnectionMode.PasswordGated,
                ConnectionOptions.SectionName,
                nameof(ConnectionOptions.JoinSecretHash));

            return Task.FromResult(AdmissionDecision.Refused);
        }

        var admitted = JoinSecretHasher.Verify(request.JoinSecret, configured);

        return Task.FromResult(admitted ? AdmissionDecision.Admitted : AdmissionDecision.Refused);
    }
}
