using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TesserChat.Server.Auth;

/// <summary>
/// Service registration for challenge-response login (§4.7).
/// </summary>
internal static class AuthExtensions
{
    /// <summary>
    /// Registers <see cref="ChallengeAuthenticator"/> and binds <see cref="AuthOptions"/>.
    /// </summary>
    public static IHostApplicationBuilder AddChallengeAuth(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services
            .AddOptions<AuthOptions>()
            .Bind(builder.Configuration.GetSection(AuthOptions.SectionName));

        // Scoped, matching the DbContext it issues and consumes challenges through.
        builder.Services.AddScoped<ChallengeAuthenticator>();

        return builder;
    }
}
