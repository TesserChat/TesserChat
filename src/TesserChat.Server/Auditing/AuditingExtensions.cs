using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TesserChat.Server.Auditing;

/// <summary>
/// Service registration for the audit log (§5.5).
/// </summary>
internal static class AuditingExtensions
{
    /// <summary>Registers <see cref="AuditLog"/>.</summary>
    public static IHostApplicationBuilder AddAuditing(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Scoped, matching the DbContext it writes through — entries are added to the caller's
        // transaction, so the two must share a context.
        builder.Services.AddScoped<AuditLog>();

        return builder;
    }
}
