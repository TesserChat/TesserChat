using System.ComponentModel;
using System.Diagnostics;

namespace TesserChat.Server.Tests.Infrastructure;

/// <summary>
/// One-time probe for a usable Docker daemon, used to decide whether the Postgres integration
/// tests run or skip (§0.3).
/// </summary>
/// <remarks>
/// <para>
/// The tests need a Linux container, which rules out the Windows and macOS CI runners: the Windows
/// runner's daemon serves Windows containers and the macOS runner has no daemon at all. Rather
/// than failing on two thirds of the matrix, they skip where Docker cannot serve them and run
/// where it can — which includes a developer machine with Docker Desktop in Linux-container mode.
/// </para>
/// <para>
/// Skipping is the failure mode worth guarding, though: a matrix that is green because it ran
/// nothing tells you less than a red one. Setting <see cref="RequireEnvironmentVariable"/> turns
/// the skip off, so the tests run and fail loudly on their own missing-Docker error. CI sets it on
/// the Linux runner, where Docker is guaranteed.
/// </para>
/// </remarks>
internal static class DockerAvailability
{
    /// <summary>Set to <c>true</c> to make an unavailable Docker a failure instead of a skip.</summary>
    public const string RequireEnvironmentVariable = "TESSERCHAT_REQUIRE_DOCKER";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<string?> LazySkipReason = new(DetermineSkipReason);

    /// <summary>
    /// Why the Docker-dependent tests should be skipped, or <c>null</c> if they should run.
    /// </summary>
    public static string? SkipReason => LazySkipReason.Value;

    private static string? DetermineSkipReason()
    {
        if (CanServeLinuxContainers())
        {
            return null;
        }

        // Requested explicitly, so let the test run and report Docker's own error rather than
        // quietly reporting success for a test that never executed.
        if (Environment.GetEnvironmentVariable(RequireEnvironmentVariable) is { } required
            && bool.TryParse(required, out var mustHaveDocker)
            && mustHaveDocker)
        {
            return null;
        }

        return "Docker is not available, so the Postgres integration tests are skipped. Start Docker "
            + $"(Linux containers), or set {RequireEnvironmentVariable}=true to make this a failure.";
    }

    /// <remarks>
    /// <c>docker version</c> rather than <c>docker info</c>: <c>info</c> exits 0 even when it
    /// could not reach a daemon at all, reporting the client half and leaving the server fields
    /// blank. It also answers the second half of the question — a daemon in Windows-container mode
    /// is running, but cannot pull the Linux <c>postgres</c> image these tests need.
    /// </remarks>
    private static bool CanServeLinuxContainers()
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            ArgumentList = { "version", "--format", "{{.Server.Os}}" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Read before waiting, so a daemon that never answers cannot outlive the timeout.
            var readServerOs = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0
                && readServerOs.GetAwaiter().GetResult().Trim()
                    .Equals("linux", StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            // No docker executable on PATH at all.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
