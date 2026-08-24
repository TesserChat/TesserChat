using TesserChat.Shared.Identity;

namespace TesserChat.Server.Accounts;

/// <summary>
/// What a prospective member presents when asking to register (§5.2).
/// </summary>
/// <param name="Identity">The public identity seeking to register.</param>
/// <param name="JoinSecret">
/// A shared joining password, when the server asks for one. <see langword="null"/> when the caller
/// offered none.
/// </param>
/// <param name="InviteToken">
/// A single-use invite token (#44), when the caller has one. <see langword="null"/> today in every
/// path — nothing issues invites yet, and no policy reads it.
/// </param>
/// <remarks>
/// <para>
/// One request type rather than a parameter per credential, so adding a way to be admitted does not
/// change the signature of every caller between the endpoint and the gate.
/// </para>
/// <para>
/// <b>These are credentials.</b> Never log an instance of this type, and never echo a field back in
/// a response.
/// </para>
/// </remarks>
internal sealed record AdmissionRequest(
    PublicIdentity Identity,
    string? JoinSecret = null,
    string? InviteToken = null);
