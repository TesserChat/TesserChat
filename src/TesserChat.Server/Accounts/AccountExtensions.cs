using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TesserChat.Server.Accounts;

/// <summary>
/// Service registration for the account layer (§5.1).
/// </summary>
internal static class AccountExtensions
{
    /// <summary>
    /// Registers <see cref="AccountRegistrar"/> and the clock it stamps registrations with.
    /// </summary>
    public static IHostApplicationBuilder AddAccounts(this IHostApplicationBuilder builder)
    {
        // Registered rather than taken from TimeProvider.System directly, so a test can substitute
        // a controlled clock without reaching into the registrar.
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Scoped, matching the DbContext it holds — a singleton would outlive its context.
        builder.Services.AddScoped<AccountRegistrar>();

        return builder;
    }
}
