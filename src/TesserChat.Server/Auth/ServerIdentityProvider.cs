using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Persistence;

namespace TesserChat.Server.Auth;

/// <summary>
/// This server's own id, cached for the paths that need it on every request (§4.7.6).
/// </summary>
/// <remarks>
/// <para>
/// Token validation checks the issuer and audience against this id on every authenticated call.
/// Reading the row each time would be a database round trip to learn a constant, so it is read once
/// and kept.
/// </para>
/// <para>
/// <b>Cached only once it exists.</b> A server can complete setup while running, so a null result is
/// re-read next time rather than cached — otherwise a server that started unconfigured would refuse
/// every token until it was restarted. Once the row exists it never changes (§5.6), so caching from
/// that point on is safe.
/// </para>
/// </remarks>
internal sealed class ServerIdentityProvider(IServiceScopeFactory scopeFactory)
{
    private Guid? _serverId;

    /// <summary>
    /// This server's id, or <see langword="null"/> while it is unconfigured.
    /// </summary>
    /// <remarks>
    /// Synchronous because its callers are synchronous — the token validation delegates offer no
    /// async form. Blocking is confined to the reads before setup completes; after that every call
    /// is a field read.
    /// </remarks>
    public Guid? GetServerId()
    {
        if (_serverId is { } cached)
        {
            return cached;
        }

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TesserChatDbContext>();

        var serverId = context.ServerInstances
            .AsNoTracking()
            .Select(instance => (Guid?)instance.Id)
            .SingleOrDefault();

        if (serverId is not null)
        {
            _serverId = serverId;
        }

        return serverId;
    }
}
