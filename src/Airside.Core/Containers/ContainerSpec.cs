using Airside.Core.Common;

namespace Airside.Core.Containers;

/// <summary>Everything needed to create a container. No Docker types appear here by design.</summary>
public sealed record ContainerSpec
{
    public required string Name { get; init; }

    public required ImageReference Image { get; init; }

    /// <summary>
    /// The command, as an argument vector. Never a command line, and never routed
    /// through <c>sh -c</c> — see <see cref="ExecRequest"/>.
    /// </summary>
    public IReadOnlyList<string>? Command { get; init; }

    public IReadOnlyList<EnvironmentEntry> Environment { get; init; } = [];

    /// <summary>Must include the full Airside label set. See <c>AirsideLabels</c>.</summary>
    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    public required ContainerLimits Limits { get; init; }

    public IReadOnlyList<VolumeMount> Mounts { get; init; } = [];

    public IReadOnlyList<PortBinding> Ports { get; init; } = [];

    public string? NetworkName { get; init; }

    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.UnlessStopped;

    public ContainerSecurity Security { get; init; } = ContainerSecurity.Default;

    public HealthProbe? HealthProbe { get; init; }
}

/// <summary>
/// An environment variable destined for a container.
/// </summary>
/// <remarks>
/// The value is always a <see cref="Secret"/>, even when
/// <paramref name="IsSensitive"/> is false, so that logging or serialising a
/// <see cref="ContainerSpec"/> can never leak a password by accident. The flag
/// exists so diagnostics can safely show the values that are genuinely harmless.
/// </remarks>
public sealed record EnvironmentEntry(string Key, Secret Value, bool IsSensitive);

/// <summary>
/// A mount into a container.
/// </summary>
/// <remarks>
/// There is deliberately no host-path variant. Bind mounts from arbitrary host
/// paths are the single most dangerous thing a control plane with the Docker
/// socket can be talked into, and the way to prevent it is to make it
/// inexpressible rather than to validate it. The system containers' own bind
/// mounts are created by the installer, not through this API.
/// </remarks>
public sealed record VolumeMount(string VolumeName, string ContainerPath, bool ReadOnly = false);

/// <param name="BindAddress">
/// Defaults to loopback. Publishing a database to <c>0.0.0.0</c> is an explicit,
/// separately confirmed choice — the default must never put one on the internet.
/// </param>
public sealed record PortBinding(
    int ContainerPort,
    int HostPort,
    string BindAddress = PortBinding.Loopback,
    PortProtocol Protocol = PortProtocol.Tcp)
{
    public const string Loopback = "127.0.0.1";
    public const string AllInterfaces = "0.0.0.0";
}

public enum PortProtocol
{
    Tcp,
    Udp,
}

/// <summary>Enforced by Docker via <c>HostConfig</c>. Storage is accounted, not enforced — ARCHITECTURE.md §5.</summary>
public sealed record ContainerLimits(long MemoryBytes, long CpuNanos);

public enum RestartPolicy
{
    No,
    OnFailure,
    UnlessStopped,
    Always,
}

/// <summary>
/// Hardening applied to every managed container.
/// </summary>
/// <param name="User">
/// Null means the image's own <c>USER</c> decides. Airside cannot force an
/// arbitrary user image to run non-root without breaking images that legitimately
/// write to root-owned paths, so it detects and warns rather than silently
/// failing to deliver a guarantee.
/// </param>
public sealed record ContainerSecurity(
    bool NoNewPrivileges,
    IReadOnlyList<string> DropCapabilities,
    bool ReadOnlyRootFilesystem,
    string? User)
{
    public static ContainerSecurity Default { get; } = new(
        NoNewPrivileges: true,
        DropCapabilities: ["ALL"],
        ReadOnlyRootFilesystem: false,
        User: null);
}

/// <param name="Command">An argument vector, never a command line.</param>
public sealed record HealthProbe(
    IReadOnlyList<string> Command,
    TimeSpan Interval,
    TimeSpan Timeout,
    int Retries,
    TimeSpan StartPeriod);

/// <summary>
/// An image, pinned by digest wherever one is known.
/// </summary>
/// <remarks>
/// A tag is not a version. <c>postgres:16</c> moves, and a restart six months
/// later silently landing on a new patch release is how a database comes back
/// refusing to start.
/// </remarks>
public sealed record ImageReference(string Repository, string Tag, string? Digest = null)
{
    public override string ToString() =>
        Digest is null ? $"{Repository}:{Tag}" : $"{Repository}@{Digest}";
}
