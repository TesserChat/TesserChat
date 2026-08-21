using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TesserChat.Server.Persistence;

/// <summary>
/// Builds a <see cref="TesserChatDbContext"/> for the <c>dotnet ef</c> tooling.
/// </summary>
/// <remarks>
/// Without this, the tooling boots the real host to find the context, which fails on a developer
/// machine that has no <c>appsettings.json</c> — and, worse, would point migration scaffolding at
/// whatever live database that config happened to name. Scaffolding a migration never connects, so
/// the placeholder connection string below is only there to satisfy the provider.
/// </remarks>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TesserChatDbContext>
{
    public TesserChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TesserChatDbContext>()
            .UseNpgsql("Host=design-time-only;Database=tesserchat")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new TesserChatDbContext(options);
    }
}
