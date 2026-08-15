namespace Airside.Core.Containers;

/// <summary>
/// The single seam between Airside and whatever actually runs containers.
/// </summary>
/// <remarks>
/// <para>
/// Docker is an implementation detail behind this interface. No Docker type
/// appears in any signature here or in any type it references, so a Podman
/// implementation or a remote agent can be dropped in without touching a caller.
/// </para>
/// <para>
/// Every method is asynchronous and takes a <see cref="CancellationToken"/>,
/// including ones that a local Docker socket answers instantly. A remote-agent
/// implementation is a network call, and the interface has to assume that from
/// the start or the migration means rewriting every caller.
/// </para>
/// <para>
/// The surface is grouped rather than flat. One seam to swap, four cohesive
/// surfaces to fake — a test that only needs volume behaviour should not have to
/// stub twenty container methods.
/// </para>
/// </remarks>
public interface IContainerRuntime
{
    IContainerOperations Containers { get; }

    IImageOperations Images { get; }

    IVolumeOperations Volumes { get; }

    INetworkOperations Networks { get; }

    Task<RuntimeInfo> GetInfoAsync(CancellationToken ct);

    /// <summary>Whether the runtime is reachable. Used by the health endpoint; never throws.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

public interface IContainerOperations
{
    /// <summary>Creates a container and returns its id. Does not start it.</summary>
    Task<string> CreateAsync(ContainerSpec spec, CancellationToken ct);

    Task StartAsync(string containerId, CancellationToken ct);

    Task StopAsync(string containerId, TimeSpan timeout, CancellationToken ct);

    Task RestartAsync(string containerId, TimeSpan timeout, CancellationToken ct);

    Task RemoveAsync(string containerId, bool force, CancellationToken ct);

    /// <summary>Returns null when no such container exists. Accepts an id or a name.</summary>
    Task<ContainerSummary?> FindAsync(string idOrName, CancellationToken ct);

    /// <summary>
    /// Lists containers carrying <c>airside.managed=true</c>, optionally narrowed
    /// by further label matches. This is what reconciliation diffs against the store.
    /// </summary>
    Task<IReadOnlyList<ContainerSummary>> ListManagedAsync(
        IReadOnlyDictionary<string, string>? labelFilters,
        CancellationToken ct);

    IAsyncEnumerable<ContainerLogLine> StreamLogsAsync(
        string containerId,
        LogQuery query,
        CancellationToken ct);

    /// <summary>
    /// Samples resource usage. <see cref="ContainerStatsSample.CpuNanos"/> is null
    /// on the first call for a container — see the remarks on that type.
    /// </summary>
    Task<ContainerStatsSample?> SampleStatsAsync(string containerId, CancellationToken ct);

    /// <summary>
    /// Runs a command inside a container, writing stdout to
    /// <paramref name="standardOutput"/> if supplied. Used for logical backups,
    /// where the payload is a stream that must not be buffered in memory.
    /// </summary>
    Task<ExecResult> ExecAsync(ExecRequest request, Stream? standardOutput, CancellationToken ct);
}

public interface IImageOperations
{
    Task<ImageSummary> PullAsync(
        ImageReference image,
        IProgress<string>? progress,
        CancellationToken ct);

    Task<ImageSummary> BuildAsync(
        ImageBuildRequest request,
        IProgress<string>? progress,
        CancellationToken ct);

    /// <summary>Returns null when the image is not present locally.</summary>
    Task<ImageSummary?> FindAsync(ImageReference image, CancellationToken ct);

    Task RemoveAsync(string imageId, bool force, CancellationToken ct);
}

public interface IVolumeOperations
{
    Task<VolumeSummary> CreateAsync(VolumeSpec spec, CancellationToken ct);

    /// <summary>Returns null when no such volume exists.</summary>
    Task<VolumeSummary?> FindAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<VolumeSummary>> ListManagedAsync(CancellationToken ct);

    Task RemoveAsync(string name, bool force, CancellationToken ct);

    /// <summary>
    /// Measures on-disk size. This is how storage accounting stays honest on hosts
    /// where per-volume limits cannot be enforced at all (ARCHITECTURE.md §5).
    /// </summary>
    Task<long> MeasureAsync(string name, CancellationToken ct);

    /// <summary>Copies a file out of a volume — the Redis RDB snapshot path.</summary>
    Task CopyFromAsync(string volumeName, string pathInVolume, Stream destination, CancellationToken ct);

    /// <summary>
    /// Copies a file into a volume. Only valid while nothing has the volume open
    /// for writing; the Redis restore flow stops the container first.
    /// </summary>
    Task CopyIntoAsync(string volumeName, string pathInVolume, Stream source, CancellationToken ct);
}

public interface INetworkOperations
{
    Task<NetworkSummary> CreateAsync(NetworkSpec spec, CancellationToken ct);

    /// <summary>Returns null when no such network exists.</summary>
    Task<NetworkSummary?> FindAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<NetworkSummary>> ListManagedAsync(CancellationToken ct);

    Task RemoveAsync(string name, CancellationToken ct);

    /// <summary>
    /// Joins a running container to a network. This is the enforcement half of a
    /// database attachment, and it needs no restart.
    /// </summary>
    Task ConnectAsync(string networkName, string containerId, CancellationToken ct);

    Task DisconnectAsync(string networkName, string containerId, CancellationToken ct);
}
