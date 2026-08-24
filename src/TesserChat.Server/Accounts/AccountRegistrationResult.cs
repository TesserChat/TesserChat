using TesserChat.Server.Persistence;

namespace TesserChat.Server.Accounts;

/// <summary>
/// The outcome of a registration attempt: a status, and the account when there is one.
/// </summary>
/// <param name="Status">Whether the attempt succeeded, and why not if it did not.</param>
/// <param name="Account">
/// The registered account, or <see langword="null"/> for any non-success status.
/// </param>
/// <param name="IsNewAccount">
/// <see langword="true"/> when this call created the account, <see langword="false"/> when the key
/// was already registered. Callers that need to distinguish "joined" from "returning" — the
/// first-run setup flow assigning Owner (§5.6), an audit entry (§5.5) — read this rather than
/// inferring it from a second query.
/// </param>
internal readonly record struct AccountRegistrationResult(
    AccountRegistrationStatus Status,
    Account? Account,
    bool IsNewAccount)
{
    /// <summary>Whether the account exists as a result of this call.</summary>
    public bool Succeeded => Status == AccountRegistrationStatus.Registered;

    internal static AccountRegistrationResult Created(Account account) => new(
        AccountRegistrationStatus.Registered,
        account,
        IsNewAccount: true);

    internal static AccountRegistrationResult AlreadyRegistered(Account account) => new(
        AccountRegistrationStatus.Registered,
        account,
        IsNewAccount: false);

    internal static AccountRegistrationResult Rejected(AccountRegistrationStatus status) => new(
        status,
        Account: null,
        IsNewAccount: false);
}
