using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TesserChat.Server.Authorization;

/// <summary>
/// Service registration for the role and permission layer (§5.3).
/// </summary>
internal static class AuthorizationExtensions
{
    /// <summary>
    /// Registers <see cref="PermissionResolver"/> and <see cref="RoleManager"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not named <c>AddAuthorization</c>: ASP.NET Core ships an extension method by
    /// that name for its own authorization services, and two identically named extensions in scope
    /// would resolve by which usings happen to be present.
    /// </remarks>
    public static IHostApplicationBuilder AddRolesAndPermissions(this IHostApplicationBuilder builder)
    {
        // Already registered by AddAccounts when both are present; TryAdd keeps either able to
        // stand alone without the second registration replacing the first.
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Scoped, matching the DbContext they hold.
        builder.Services.AddScoped<PermissionResolver>();
        builder.Services.AddScoped<RoleManager>();

        return builder;
    }
}
