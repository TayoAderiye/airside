using Airside.Core.Common;

namespace Airside.Core.Containers;

public sealed record ContainerSummary(
    string Id,
    string Name,
    string Image,
    string? ImageDigest,
    ContainerRunState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    int? ExitCode,
    ContainerHealth Health,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Networks,
    IReadOnlyList<PublishedPort> Ports);

/// <summary>A host port a container has claimed.</summary>
/// <remarks>
/// Carried so pre-flight can name what is holding port 80. The API runs in its
/// own network namespace, so it cannot see the host's listening sockets — asking
/// Docker for its port bindings is the only view available from in here, and it
/// covers the common case of a leftover container from a previous stack.
/// </remarks>
public sealed record PublishedPort(int HostPort, int ContainerPort, string BindAddress);

public enum ContainerRunState
{
    Created,
    Running,
    Paused,
    Restarting,
    Exited,
    Dead,
}

public enum ContainerHealth
{
    /// <summary>The container declares no health check.</summary>
    None,
    Starting,
    Healthy,
    Unhealthy,
}

public sealed record VolumeSpec(
    string Name,
    IReadOnlyDictionary<string, string> Labels);

public sealed record VolumeSummary(
    string Name,
    string MountPoint,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string> Labels);

public sealed record NetworkSpec(
    string Name,
    IReadOnlyDictionary<string, string> Labels,
    bool Internal = false);

public sealed record NetworkSummary(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> ConnectedContainerIds);

/// <summary>
/// A command to run inside a running container.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Argv"/> is an argument vector. There is no string-command
/// overload anywhere in Airside, and nothing invokes <c>sh -c</c>: removing the
/// tool is how the "never build shell commands from user input" rule is made to
/// hold rather than merely asserted.
/// </para>
/// <para>
/// Credentials go in <paramref name="Environment"/>, never in
/// <paramref name="Argv"/> — arguments are visible in the container's process
/// list to anything else running in it.
/// </para>
/// </remarks>
public sealed record ExecRequest(
    string ContainerId,
    IReadOnlyList<string> Argv,
    IReadOnlyList<EnvironmentEntry>? Environment = null,
    string? WorkingDirectory = null);

/// <param name="StandardError">
/// Captured separately from stdout. Docker's exec stream is multiplexed with
/// 8-byte frame headers when no TTY is attached; a reader that ignores them
/// concatenates stderr into the payload and silently corrupts every dump taken
/// while the engine emits a warning. Demuxing is the runtime's job, and it has a
/// dedicated test.
/// </param>
public sealed record ExecResult(int ExitCode, string StandardError);

public sealed record ContainerLogLine(
    DateTimeOffset Timestamp,
    LogSource Stream,
    string Text);

public enum LogSource
{
    StandardOutput,
    StandardError,
}

public sealed record LogQuery
{
    public int? TailLines { get; init; } = 200;

    public DateTimeOffset? Since { get; init; }

    public bool Follow { get; init; }

    public bool IncludeStandardError { get; init; } = true;
}

/// <summary>
/// A point-in-time resource sample for one container.
/// </summary>
/// <param name="CpuNanos">
/// Null until two samples exist. Docker's non-streaming stats call has no
/// previous CPU reading, so the first sample can only yield a meaningless 0% —
/// this returns null rather than a plausible lie.
/// </param>
public sealed record ContainerStatsSample(
    string ContainerId,
    DateTimeOffset SampledAt,
    long? CpuNanos,
    long MemoryBytes,
    long MemoryLimitBytes);

public sealed record ImageBuildRequest(
    string ContextPath,
    string DockerfilePath,
    ImageReference Tag,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<EnvironmentEntry>? BuildArguments = null);

public sealed record ImageSummary(
    string Id,
    string Digest,
    IReadOnlyList<string> Tags,
    long SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record RuntimeInfo(
    string ApiVersion,
    string ServerVersion,
    string OperatingSystem,
    string KernelVersion,
    int TotalCpuCount,
    long TotalMemoryBytes);

/// <summary>
/// Thrown when the container runtime itself is unreachable or misbehaving.
/// </summary>
/// <remarks>
/// The runtime layer throws for infrastructure failure and returns null for
/// absence; it does not return <see cref="Result{T}"/>. A vanished Docker daemon
/// is genuinely exceptional, and threading it through every call site as an
/// expected value would drown the failures that callers can actually act on.
/// Services translate this into <c>runtime.unavailable</c>.
/// </remarks>
public sealed class ContainerRuntimeException : Exception
{
    public ContainerRuntimeException()
    {
    }

    public ContainerRuntimeException(string message)
        : base(message)
    {
    }

    public ContainerRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
