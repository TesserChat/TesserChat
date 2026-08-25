namespace TesserChat.Server;

/// <summary>
/// The <c>healthcheck</c> startup command, used as the container's <c>HEALTHCHECK</c> (§5.6).
/// </summary>
/// <remarks>
/// <para>
/// The runtime image carries no <c>curl</c> or <c>wget</c> — the .NET runtime images are
/// deliberately minimal — so the probe has to be something already in the image. Running the app's
/// own binary against its own endpoint costs nothing extra and keeps the image free of tools
/// installed solely to check on it.
/// </para>
/// <para>
/// Probes the same unauthenticated <c>/health</c> endpoint as any other caller, so it verifies the
/// server is actually serving rather than merely that a process exists.
/// </para>
/// </remarks>
internal static class HealthCheckCommand
{
    private const string CommandName = "healthcheck";

    /// <summary>How long to wait before treating the server as unhealthy.</summary>
    /// <remarks>
    /// Shorter than the <c>HEALTHCHECK</c> timeout in the Dockerfile, so a hung request fails on
    /// this timeout with a clear exit code rather than being killed partway by Docker.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Handles the command if <paramref name="args"/> names it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the command was handled and the process should exit with
    /// <paramref name="exitCode"/>; <see langword="false"/> to boot the server normally.
    /// </returns>
    public static bool TryHandle(string[] args, TextWriter error, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(error);

        exitCode = 0;

        if (args.Length == 0 || !string.Equals(args[0], CommandName, StringComparison.Ordinal))
        {
            return false;
        }

        exitCode = Probe(error) ? 0 : 1;
        return true;
    }

    private static bool Probe(TextWriter error)
    {
        // The port the server listens on inside the container. Read from the environment rather
        // than hardcoded, so overriding ASPNETCORE_HTTP_PORTS does not silently break the probe.
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";

        // Only the first, if several are configured — probing one is enough to know the server is
        // serving.
        port = port.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "8080";

        try
        {
            using var client = new HttpClient { Timeout = ProbeTimeout };

            // Loopback: the probe runs inside the container the server runs in.
            using var response = client.GetAsync($"http://localhost:{port}/health").GetAwaiter().GetResult();

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            error.WriteLine($"health check failed: HTTP {(int)response.StatusCode}");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Not yet listening, or not answering in time. Both mean unhealthy, and both are
            // expected while a container is still starting — which is what start-period covers.
            error.WriteLine($"health check failed: {ex.Message}");
            return false;
        }
    }
}
