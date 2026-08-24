using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TesserChat.Server.Setup;

/// <summary>
/// Registration and startup reporting for first-run setup (§5.6).
/// </summary>
internal static class SetupExtensions
{
    /// <summary>Registers <see cref="SetupService"/> and binds <see cref="SetupOptions"/>.</summary>
    public static IHostApplicationBuilder AddSetup(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services
            .AddOptions<SetupOptions>()
            .Bind(builder.Configuration.GetSection(SetupOptions.SectionName));

        // Scoped, matching the DbContext it holds.
        builder.Services.AddScoped<SetupService>();

        return builder;
    }

    /// <summary>
    /// Reports on startup whether the server still needs setting up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A self-hoster's first contact with a fresh container is <c>docker logs</c>, so what it says
    /// there is the onboarding path (§5.6). It also carries the one warning that matters: an
    /// unconfigured server with no pinned key will hand Owner to whoever reaches it first, which is
    /// fine on a machine nobody else can reach and not fine on a published port.
    /// </para>
    /// <para>
    /// <b>Advisory only — it never prevents startup.</b> This reports on state rather than
    /// establishing any, so a database it cannot read costs a log line and nothing else. Startup
    /// already fails loudly on an unusable database where that matters: no connection string is a
    /// build-time throw, and migrations run before this (§5.4).
    /// </para>
    /// </remarks>
    public static async Task ReportSetupStateAsync(
        this IHost host,
        CancellationToken cancellationToken = default)
    {
        await using var scope = host.Services.CreateAsyncScope();

        var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SetupOptions>>().CurrentValue;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SetupExtensions).FullName!);

        bool setupRequired;
        try
        {
            setupRequired = await setup.IsSetupRequiredAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                ex,
                "Could not read setup state at startup; skipping the setup report.");
            return;
        }

        if (!setupRequired)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.OwnerPublicKey))
        {
            logger.LogInformation(
                "This server has not been set up yet. Setup is reserved for the public key pinned "
                + "as {Section}:{Setting}; no other key can claim ownership.",
                SetupOptions.SectionName,
                nameof(SetupOptions.OwnerPublicKey));

            return;
        }

        // Deliberately loud. The tradeoff is reasonable and is the documented default, but it
        // should be a decision the operator knows they are making rather than one they discover.
        logger.LogWarning(
            "This server has not been set up yet, and no owner key is pinned: the first client to "
            + "complete setup becomes the Owner. That is fine on a machine nothing else can reach. "
            + "If this server is published to a network you do not control, stop it and set "
            + "{Section}:{Setting} to your public key first.",
            SetupOptions.SectionName,
            nameof(SetupOptions.OwnerPublicKey));
    }
}
