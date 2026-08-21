namespace TesserChat.Server.Tests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when Docker cannot serve Linux containers.
/// </summary>
/// <remarks>
/// The skip decision is made once per test run and cached — see <see cref="DockerAvailability"/>
/// for why skipping rather than failing is the right default, and how to invert it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute() => Skip = DockerAvailability.SkipReason;
}
