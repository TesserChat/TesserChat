using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TesserChat.Server.Realtime;

/// <summary>
/// Service registration and routing for the real-time transport (§6).
/// </summary>
internal static class RealtimeExtensions
{
    /// <summary>
    /// The hub's path.
    /// </summary>
    /// <remarks>
    /// Under <c>/hubs</c> deliberately: that prefix is what
    /// <c>AuthExtensions.IsHubRequest</c> allows a query-string token for (§4.7.6). A hub mapped
    /// anywhere else would be unreachable by a SignalR client, which cannot set an
    /// <c>Authorization</c> header on the WebSocket handshake.
    /// </remarks>
    public const string HubPath = "/hubs/tesserchat";

    /// <summary>
    /// Registers SignalR and the connection registry.
    /// </summary>
    /// <remarks>
    /// Not named <c>AddSignalR</c>: ASP.NET Core ships an extension by that name, and two in scope
    /// would resolve by whichever usings happen to be present — the same reason
    /// <c>AddRolesAndPermissions</c> is not called <c>AddAuthorization</c>.
    /// </remarks>
    public static IHostApplicationBuilder AddRealtime(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);

        // Singleton: it is the process's record of who is connected, and a scoped one would be a
        // fresh empty registry per hub method call.
        builder.Services.AddSingleton<ConnectionRegistry>();

        builder.Services.AddSignalR();

        return builder;
    }

    /// <summary>
    /// Maps the hub at <see cref="HubPath"/>.
    /// </summary>
    /// <remarks>
    /// No <c>RequireAuthorization</c> here — the hub carries <c>[Authorize]</c> on the class, which
    /// applies to the connection rather than to a route, and is what refuses an unauthenticated
    /// handshake.
    /// </remarks>
    public static IEndpointRouteBuilder MapRealtime(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHub<TesserChatHub>(HubPath);

        return endpoints;
    }
}
