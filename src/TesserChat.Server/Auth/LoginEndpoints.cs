using System.Buffers.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using TesserChat.Shared.Auth;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Auth;

/// <summary>
/// The two unauthenticated endpoints that turn a keypair into a session (§4.7).
/// </summary>
/// <remarks>
/// <para>
/// Both are necessarily open to anyone — they are how a member authenticates, so requiring
/// authentication to reach them would be circular. Everything they can be asked is therefore
/// assumed hostile.
/// </para>
/// <para>
/// <b>A failed login is answered with one undifferentiated 401.</b> The authenticator distinguishes
/// an expired challenge from a replayed one from an unregistered key (see <see cref="LoginStatus"/>),
/// and none of that reaches the caller: telling a stranger that their key is unknown, rather than
/// that their signature was bad, turns login into a test for whether a given public key is
/// registered here. That is the enumeration boundary §5.2 draws around admission and §8.2 draws
/// around presence, and it is why the reasons stay in the server's own logs.
/// </para>
/// </remarks>
internal static class LoginEndpoints
{
    /// <summary>Maps <c>POST /auth/challenge</c> and <c>POST /auth/login</c>.</summary>
    public static IEndpointRouteBuilder MapLogin(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/auth").AllowAnonymous();

        group.MapPost("/challenge", IssueChallengeAsync);
        group.MapPost("/login", LoginAsync);

        return endpoints;
    }

    /// <summary>
    /// Hands out a challenge to sign.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>POST rather than GET</b>, because it writes: every call inserts a nonce row. A GET would
    /// also invite caching by an intermediary, and a cached challenge is one that has been handed to
    /// more than one caller.
    /// </para>
    /// <para>
    /// <b>Takes no identity</b>, so it cannot be used to ask whether a key is registered here
    /// (§4.7.3). Who is logging in is established from the signature, not from a claim made before
    /// one exists.
    /// </para>
    /// </remarks>
    private static async Task<IResult> IssueChallengeAsync(
        [FromServices] ChallengeAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var issued = await authenticator.IssueChallengeAsync(cancellationToken);

        if (issued is null)
        {
            // No identity to bind a signature to, so no challenge can be scoped. The way in is to
            // complete setup (§5.6), which is a different endpoint and a different flow.
            return Results.Problem(
                title: "This server has not been set up yet.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var challenge = issued.Value;

        return Results.Ok(new ChallengeResponse(
            challenge.ServerId.ToString("D"),
            Base64Url.EncodeToString(challenge.Nonce),
            challenge.ExpiresAt.ToUniversalTime().ToString("O")));
    }

    /// <summary>
    /// Verifies a signed challenge and issues a session token.
    /// </summary>
    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest request,
        [FromServices] ChallengeAuthenticator authenticator,
        [FromServices] SessionTokenIssuer tokens,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(LoginEndpoints).FullName!);

        if (request is null
            || !TryDecode(request.PublicKey, IdentityKeyPair.PublicKeySize, out var publicKey)
            || !TryDecode(request.Nonce, LoginChallenge.NonceSize, out var nonce)
            || !TryDecode(request.Signature, IdentityKeyPair.SignatureSize, out var signature))
        {
            // Malformed input is refused exactly as a bad signature is. A distinct "your base64 is
            // wrong" would be harmless on its own, but it is one more thing a caller can tell apart,
            // and the set of things they can tell apart is what an oracle is built from.
            return Rejected();
        }

        var result = await authenticator.LoginAsync(publicKey, nonce, signature, cancellationToken);

        if (!result.Succeeded)
        {
            // The reason goes to the operator's log, never to the caller.
            logger.LogInformation("A login attempt was refused: {Status}.", result.Status);
            return Rejected();
        }

        var token = await tokens.IssueAsync(result.AccountId, cancellationToken);

        logger.LogInformation(
            "Issued a session token to {AccountId}, expiring at {ExpiresAt}.",
            result.AccountId,
            token.ExpiresAt);

        return Results.Ok(new LoginResponse(
            token.Value,
            result.AccountId.ToString("D"),
            token.ExpiresAtIso8601(),
            token.SecondsUntilExpiry(timeProvider.GetUtcNow())));
    }

    /// <summary>
    /// The single answer to every failed login.
    /// </summary>
    /// <remarks>
    /// One body and one status for every refusal, so the response distinguishes nothing that the
    /// caller was not already entitled to know.
    /// </remarks>
    private static IResult Rejected()
        => Results.Problem(
            title: "Authentication failed.",
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Decodes a base64url field, requiring an exact length.
    /// </summary>
    /// <remarks>
    /// The length check belongs here rather than deeper in: a key or signature of the wrong size
    /// cannot verify, and refusing it at the edge keeps malformed input from reaching the crypto at
    /// all.
    /// </remarks>
    private static bool TryDecode(string? value, int expectedLength, out byte[] decoded)
    {
        decoded = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(value.Trim());
            if (bytes.Length != expectedLength)
            {
                return false;
            }

            decoded = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The account id a validated token authenticates, or <see langword="null"/> if the principal
    /// carries none.
    /// </summary>
    /// <remarks>
    /// The claim is written by <see cref="SessionTokenIssuer"/> and validated before a principal
    /// exists, so a malformed value here would mean this server signed one — hence null rather than
    /// a throw, and callers treat it as unauthenticated.
    /// </remarks>
    public static Guid? GetAccountId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirstValue(SessionTokenIssuer.AccountClaim);

        return Guid.TryParse(value, out var accountId) ? accountId : null;
    }
}
