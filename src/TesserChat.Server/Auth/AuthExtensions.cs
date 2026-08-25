using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace TesserChat.Server.Auth;

/// <summary>
/// Service registration for challenge-response login and session tokens (§4.7).
/// </summary>
internal static class AuthExtensions
{
    /// <summary>
    /// Query-string parameter a SignalR client sends its token in.
    /// </summary>
    /// <remarks>
    /// The name SignalR's own <c>AccessTokenProvider</c> uses; it is not configurable client-side,
    /// so the server matches it.
    /// </remarks>
    private const string AccessTokenQueryParameter = "access_token";

    /// <summary>
    /// Registers <see cref="ChallengeAuthenticator"/>, session token issuance and validation, and
    /// binds <see cref="AuthOptions"/>.
    /// </summary>
    public static IHostApplicationBuilder AddChallengeAuth(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services
            .AddOptions<AuthOptions>()
            .Bind(builder.Configuration.GetSection(AuthOptions.SectionName));

        // Scoped, matching the DbContext it issues and consumes challenges through.
        builder.Services.AddScoped<ChallengeAuthenticator>();
        builder.Services.AddScoped<SessionTokenIssuer>();

        // Singletons: both cache values that are constant once written, and the cache is only
        // useful if it is process-wide. Each resolves its own scope for the reads it does.
        builder.Services.AddSingleton<TokenSigningKeyStore>();
        builder.Services.AddSingleton<ServerIdentityProvider>();

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // Configured through IConfigureNamedOptions rather than an inline callback, so it can take
        // the real container by injection. An inline callback has no access to one, and reaching for
        // BuildServiceProvider() to get around that would construct a *second* container — giving
        // the validation path its own TokenSigningKeyStore, with its own cache, separate from the
        // one the issuer signs with.
        builder.Services
            .AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureSessionTokenBearer>();

        builder.Services.AddAuthorization();

        return builder;
    }

    /// <summary>
    /// Whether a path is a SignalR hub.
    /// </summary>
    /// <remarks>
    /// No hub is mapped yet (#16). This exists now so the query-string token path is written once,
    /// with its restriction, rather than being added later next to the hub and quietly applying to
    /// everything.
    /// </remarks>
    private static bool IsHubRequest(PathString path)
        => path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies this server's token rules to the bearer scheme (§4.7.6).
    /// </summary>
    private sealed class ConfigureSessionTokenBearer(
        IServiceProvider services,
        IOptionsMonitor<AuthOptions> authOptions)
        : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

        public void Configure(string? name, JwtBearerOptions options)
        {
            // Only the scheme this server authenticates with; another scheme added later gets its
            // own rules rather than inheriting these.
            if (name != JwtBearerDefaults.AuthenticationScheme)
            {
                return;
            }

            ConfigureBearer(services, authOptions.CurrentValue, options);
        }
    }

    private static void ConfigureBearer(
        IServiceProvider services,
        AuthOptions authOptions,
        JwtBearerOptions options)
    {
        // Built once, but every value it depends on is resolved per token — see
        // SessionTokenValidation.
        options.TokenValidationParameters =
            SessionTokenValidation.Build(services, authOptions.ClockSkew);

        // The modern handler. The legacy one rewrites `sub` into a Microsoft-specific claim URI, so
        // the claim this server writes would not be the claim it reads back.
        options.MapInboundClaims = false;
        options.TokenHandlers.Clear();
        options.TokenHandlers.Add(new JsonWebTokenHandler());

        // A rejected token is answered with a bare 401. The default adds a
        // `WWW-Authenticate: Bearer error="invalid_token", error_description="..."` header naming
        // what was wrong — expired, bad signature, wrong issuer — which is exactly the distinction
        // the login endpoint refuses to make (§4.7.4). Suppressing it keeps one rejection meaning
        // one thing.
        options.IncludeErrorDetails = false;

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // SignalR cannot set an Authorization header on the WebSocket handshake, so its
                // client sends the token in the query string instead. Accepted only for requests to
                // a hub path, so an ordinary REST endpoint cannot be authenticated by a token in a
                // URL — those end up in server logs, browser history, and referrer headers.
                var token = context.Request.Query[AccessTokenQueryParameter];

                if (!string.IsNullOrEmpty(token) && IsHubRequest(context.Request.Path))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    }
}
