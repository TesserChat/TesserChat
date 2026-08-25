namespace TesserChat.Server.Auth;

/// <summary>
/// The challenge handed to a client that asked to log in (§4.7).
/// </summary>
/// <param name="ServerId">
/// This server's id, which the client binds into its signature. A client that already knows this
/// server should check it matches: a changed id means a different deployment, not a moved one
/// (§4.7.2).
/// </param>
/// <param name="Nonce">The challenge bytes, base64url.</param>
/// <param name="ExpiresAt">
/// When the challenge stops being accepted, ISO-8601. Sent so a client can tell an expired
/// challenge from a rejected signature without guessing.
/// </param>
internal sealed record ChallengeResponse(string ServerId, string Nonce, string ExpiresAt);

/// <summary>
/// A signed challenge, presented to complete a login (§4.7).
/// </summary>
/// <param name="PublicKey">The claimed Ed25519 signing key, base64url.</param>
/// <param name="Nonce">The challenge this server issued, base64url.</param>
/// <param name="Signature">That challenge's payload signed by the claimed key, base64url.</param>
internal sealed record LoginRequest(string? PublicKey, string? Nonce, string? Signature);

/// <summary>
/// A session token, returned on a successful login (§4.7.6).
/// </summary>
/// <param name="Token">The bearer token for REST calls and the SignalR hub.</param>
/// <param name="AccountId">
/// The account the token authenticates. The client can decode this from the token itself; it is
/// returned so it does not have to parse a JWT to learn its own id.
/// </param>
/// <param name="ExpiresAt">When the token expires, ISO-8601.</param>
/// <param name="ExpiresIn">
/// Seconds until expiry. Redundant with <paramref name="ExpiresAt"/> on purpose: a client with a
/// skewed clock can schedule re-authentication off a duration when it cannot off a timestamp.
/// </param>
internal sealed record LoginResponse(
    string Token,
    string AccountId,
    string ExpiresAt,
    long ExpiresIn);
