namespace TesserChat.Server.Accounts;

/// <summary>
/// What a caller presents in order to be admitted to a restricted server (§5.2).
/// </summary>
/// <param name="JoinSecret">The shared joining password, on a password-gated server.</param>
/// <param name="InviteToken">
/// A single-use invite token (#44). Accepted by the shape today but read by nothing — invites are
/// not implemented.
/// </param>
/// <remarks>
/// <para>
/// Separate from <see cref="AdmissionRequest"/>: this is what a caller supplies, while the request
/// is what the gate evaluates, and the identity is added between them by the registrar rather than
/// being the caller's to assert twice.
/// </para>
/// <para>
/// <b>These are secrets.</b> Never log an instance, and never echo a field back in a response.
/// </para>
/// </remarks>
internal sealed record AdmissionCredentials(string? JoinSecret = null, string? InviteToken = null);
