using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TesserChat.Server.Rooms;

/// <summary>
/// Service registration for room chat (§5.4).
/// </summary>
internal static class RoomExtensions
{
    /// <summary>
    /// Registers the room manager.
    /// </summary>
    /// <remarks>
    /// Scoped, because it takes the scoped <c>TesserChatDbContext</c>. A singleton would capture one
    /// context for the process, which is the standard way to end up with a shared change tracker
    /// across concurrent requests.
    /// </remarks>
    public static IHostApplicationBuilder AddRooms(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddScoped<RoomManager>();

        return builder;
    }
}
