namespace TesserChat.Server.Tests.Infrastructure;

/// <summary>
/// A <see cref="TheoryAttribute"/> that skips itself when Docker cannot serve Linux containers.
/// </summary>
/// <remarks>
/// The data-driven counterpart to <see cref="RequiresDockerFactAttribute"/>, sharing its skip
/// decision — see <see cref="DockerAvailability"/> for why skipping rather than failing is the
/// right default, and how to invert it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
{
    public RequiresDockerTheoryAttribute() => Skip = DockerAvailability.SkipReason;
}
