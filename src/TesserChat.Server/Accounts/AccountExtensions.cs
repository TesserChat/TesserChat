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

        builder.Services
            .AddOptions<ConnectionOptions>()
            .Bind(builder.Configuration.GetSection(ConnectionOptions.SectionName));

        // One policy per admission path (§5.2). Registered as a set rather than selected here, so
        // AdmissionGate can pick per request and a mode change needs no restart — and so an invite
        // policy (#44) is an added registration rather than an edited switch.
        builder.Services.AddSingleton<IAdmissionPolicy, OpenAdmissionPolicy>();
        builder.Services.AddSingleton<IAdmissionPolicy, PasswordGatedAdmissionPolicy>();
        builder.Services.AddSingleton<IAdmissionPolicy, AllowlistAdmissionPolicy>();

        // Singleton: it holds only the policy set and an options monitor, no per-request state.
        builder.Services.AddSingleton<AdmissionGate>();

        return builder;
    }
}
