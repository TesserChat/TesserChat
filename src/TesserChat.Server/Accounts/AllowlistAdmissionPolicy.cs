using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Accounts;

/// <summary>
/// Admits only public keys the operator has pre-approved (§5.2 mode 3).
/// </summary>
/// <remarks>
/// <para>
/// The strictest mode: a key not on the list cannot register, with or without any other credential.
/// It requires the operator to hold each prospective member's public key <i>before</i> they join,
/// which is the mode's real cost — invites (#44) exist partly to remove it.
/// </para>
/// <para>
/// The list gates registration only. Removing a key from it does not remove an account that has
/// already registered; that is kicking or banning, a separate action (§5.5).
/// </para>
/// </remarks>
internal sealed class AllowlistAdmissionPolicy(
    IOptionsMonitor<ConnectionOptions> options,
    ILogger<AllowlistAdmissionPolicy> logger) : IAdmissionPolicy
{
    /// <inheritdoc />
    public ConnectionMode Mode => ConnectionMode.AllowlistOnly;

    /// <inheritdoc />
    public Task<AdmissionDecision> EvaluateAsync(
        AdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var configured = options.CurrentValue.Allowlist;

        if (configured.Count == 0)
        {
            // An empty allowlist genuinely admits nobody — that is what it says. Logged because it
            // is more often an unfinished configuration than an intent, and a server nobody can
            // join gives an operator no clue why.
            logger.LogWarning(
                "Connection mode is {Mode} but {Section}:{Setting} is empty; no key can register.",
                ConnectionMode.AllowlistOnly,
                ConnectionOptions.SectionName,
                nameof(ConnectionOptions.Allowlist));

            return Task.FromResult(AdmissionDecision.Refused);
        }

        var candidate = request.Identity.SigningKey;
        var skipped = 0;
        var admitted = false;

        foreach (var entry in configured)
        {
            if (!TryDecodeSigningKey(entry, out var allowed))
            {
                skipped++;
                continue;
            }

            // Constant-time, and every entry is checked rather than breaking on the first match:
            // both keep the time taken independent of where in the list a key sits, so timing does
            // not reveal the list's contents or ordering.
            if (CryptographicOperations.FixedTimeEquals(candidate, allowed))
            {
                admitted = true;
            }
        }

        if (skipped > 0)
        {
            // Individually ignored rather than fatal: one typo in a long list should not take a
            // server offline. Reported in aggregate so it is visible without naming the entries.
            logger.LogWarning(
                "{Count} entr{Suffix} in {Section}:{Setting} could not be read as a public key and "
                + "{Verb} ignored.",
                skipped,
                skipped == 1 ? "y" : "ies",
                ConnectionOptions.SectionName,
                nameof(ConnectionOptions.Allowlist),
                skipped == 1 ? "was" : "were");
        }

        return Task.FromResult(admitted ? AdmissionDecision.Admitted : AdmissionDecision.Refused);
    }

    /// <summary>
    /// Decodes an allowlist entry into a raw Ed25519 public key.
    /// </summary>
    /// <remarks>
    /// Accepts either a bare signing key or a full shareable identity token, since an operator will
    /// paste whichever form the prospective member sent them. A token carries both keys
    /// concatenated (<see cref="PublicIdentity.ToShareableString"/>), and the signing key is its
    /// first half.
    /// </remarks>
    private static bool TryDecodeSigningKey(string? entry, out byte[] signingKey)
    {
        signingKey = [];

        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(entry.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length == IdentityKeyPair.PublicKeySize)
        {
            signingKey = bytes;
            return true;
        }

        if (bytes.Length == IdentityKeyPair.PublicKeySize * 2)
        {
            signingKey = bytes[..IdentityKeyPair.PublicKeySize];
            return true;
        }

        return false;
    }
}
