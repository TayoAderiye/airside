using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Jobs;
using Airside.Core.Naming;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Jobs;

public static class DatabaseJobTypes
{
    public const string Provision = "database.provision";
    public const string Start = "database.start";
    public const string Stop = "database.stop";
    public const string Restart = "database.restart";
    public const string Resize = "database.resize";
    public const string Delete = "database.delete";
}

/// <summary>Everything a handler needs, resolved by the caller before the job runs.</summary>
public sealed record DatabaseProvisionPayload(Guid WorkloadId);

public sealed record DatabaseLifecyclePayload(Guid WorkloadId);

public sealed record DatabaseDeletePayload(Guid WorkloadId, bool DeleteVolume);

public sealed record DatabaseResizePayload(Guid WorkloadId, long CpuNanos, long MemoryBytes, long StorageBytes);

/// <summary>
/// Reads and writes the workload rows a handler operates on.
/// </summary>
/// <remarks>
/// Defined here rather than taken as a DbContext so that Airside.Runtime keeps
/// its rule of never referencing EF Core. The implementation lives in the API's
/// composition layer.
/// </remarks>
public interface IDatabaseWorkloadStore
{
    Task<DatabaseWorkloadSnapshot?> GetAsync(Guid workloadId, CancellationToken ct);

    Task SetStateAsync(Guid workloadId, string state, CancellationToken ct);

    Task RecordProvisionedAsync(
        Guid workloadId,
        string containerId,
        string? imageDigest,
        string networkId,
        CancellationToken ct);

    Task RecordLimitsAsync(Guid workloadId, long cpuNanos, long memoryBytes, long storageBytes, CancellationToken ct);

    /// <summary>Marks the workload deleted and either removes or orphans its volumes.</summary>
    Task RecordDeletedAsync(Guid workloadId, bool volumesRemoved, CancellationToken ct);
}

/// <summary>A workload flattened into what a handler needs, with the password already decrypted.</summary>
/// <param name="ImageDigest">
/// The digest recorded at provision. Once set, every later resolution goes
/// through it rather than the tag — a tag moves, and a re-provision landing on a
/// different build than the one the data was created under is how a database
/// comes back refusing to start.
/// </param>
public sealed record DatabaseWorkloadSnapshot(
    Guid Id,
    Slug Slug,
    string DisplayName,
    DatabaseEngineKind Engine,
    DatabaseProvisionSpec Spec,
    string? ContainerId,
    string DataVolumeName,
    string NetworkName,
    string ContainerName,
    string? ImageDigest = null);

/// <summary>
/// Creates a database: network, volume, container, start, wait for health.
/// </summary>
/// <remarks>
/// <para>
/// Every resource is tracked the moment it is created, so a failure at any step —
/// including the health check — unwinds exactly what this job made and nothing
/// else. That is the difference between a failed provision that cleans up after
/// itself and one that leaves an orphaned container, volume, and network for
/// reconciliation to report later with no explanation of where they came from.
/// </para>
/// <para>
/// Idempotent by workload id: every step looks for what it would create before
/// creating it, so a re-run after a process restart converges rather than
/// producing a second container.
/// </para>
/// </remarks>
public sealed class DatabaseProvisionHandler(
    IContainerRuntime runtime,
    IDatabaseEngineRegistry engines,
    IDatabaseWorkloadStore store,
    ILogger<DatabaseProvisionHandler> logger) : IJobHandler
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(5);

    public string JobType => DatabaseJobTypes.Provision;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<DatabaseProvisionPayload>();
        var workload = await store.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);

        if (workload is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The workload no longer exists.");
        }

        var engine = engines.Get(workload.Engine);

        await context.ReportProgressAsync(5, "Pulling image", ct).ConfigureAwait(false);

        var image = ResolveImage(workload, engine);
        var pulled = await runtime.Images
            .PullAsync(image, new Progress<string>(_ => { }), ct)
            .ConfigureAwait(false);

        await context.LogStepAsync("pull", $"Pulled {image} ({pulled.Digest})", ct).ConfigureAwait(false);

        await context.ReportProgressAsync(20, "Creating network", ct).ConfigureAwait(false);
        var network = await EnsureNetworkAsync(context, workload, ct).ConfigureAwait(false);

        await context.ReportProgressAsync(35, "Creating volume", ct).ConfigureAwait(false);
        await EnsureVolumeAsync(context, workload, ct).ConfigureAwait(false);

        await context.ReportProgressAsync(50, "Creating container", ct).ConfigureAwait(false);
        var containerId = await EnsureContainerAsync(context, workload, engine, ct).ConfigureAwait(false);

        await context.ReportProgressAsync(65, "Starting", ct).ConfigureAwait(false);
        await runtime.Containers.StartAsync(containerId, ct).ConfigureAwait(false);

        await context.ReportProgressAsync(75, "Waiting for health check", ct).ConfigureAwait(false);
        var healthy = await WaitForHealthAsync(context, containerId, ct).ConfigureAwait(false);

        if (!healthy)
        {
            return new Error(
                ErrorCodes.ApplicationHealthCheckFailed,
                "The database started but never became healthy. It has been cleaned up.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["timeoutSeconds"] = (int)HealthTimeout.TotalSeconds,
                });
        }

        await store.RecordProvisionedAsync(workload.Id, containerId, pulled.Digest, network.Id, ct)
            .ConfigureAwait(false);
        await store.SetStateAsync(workload.Id, Core.Workloads.DatabaseState.Running.ToString(), ct)
            .ConfigureAwait(false);

        await context.ReportProgressAsync(100, "Running", ct).ConfigureAwait(false);
        return Result.Ok();
    }

    /// <summary>
    /// Decides which image this provision pulls.
    /// </summary>
    /// <remarks>
    /// The order matters. An already-recorded digest wins over everything,
    /// because a re-run of a provision must land on the same build the volume was
    /// initialised by. A custom image bypasses variant resolution entirely —
    /// Airside cannot reason about what is inside it. Only then does the engine
    /// resolve a tag from version and variant.
    /// </remarks>
    private static ImageReference ResolveImage(DatabaseWorkloadSnapshot workload, IDatabaseEngine engine)
    {
        if (!string.IsNullOrEmpty(workload.ImageDigest))
        {
            return ImageReference.Parse(workload.ImageDigest);
        }

        if (!string.IsNullOrWhiteSpace(workload.Spec.CustomImage))
        {
            return ImageReference.Parse(workload.Spec.CustomImage);
        }

        return engine.ResolveImage(
            workload.Spec.Version,
            workload.Spec.Variant ?? engine.Capabilities.DefaultVariant);
    }

    private async Task<NetworkSummary> EnsureNetworkAsync(
        IJobContext context,
        DatabaseWorkloadSnapshot workload,
        CancellationToken ct)
    {
        var existing = await runtime.Networks.FindAsync(workload.NetworkName, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            // Pre-existing: tracked so compensation knows about it, but flagged as
            // not ours so it is left alone on cleanup.
            await context.TrackResourceAsync(JobResourceKind.Network, workload.NetworkName, false, ct)
                .ConfigureAwait(false);
            return existing;
        }

        var created = await runtime.Networks
            .CreateAsync(new NetworkSpec(workload.NetworkName, Labels(workload)), ct)
            .ConfigureAwait(false);

        await context.TrackResourceAsync(JobResourceKind.Network, workload.NetworkName, true, ct)
            .ConfigureAwait(false);

        return created;
    }

    private async Task EnsureVolumeAsync(
        IJobContext context,
        DatabaseWorkloadSnapshot workload,
        CancellationToken ct)
    {
        var existing = await runtime.Volumes.FindAsync(workload.DataVolumeName, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            // Critically, createdByThisJob is false. A retry must never delete a
            // volume that already held data — that is the difference between a
            // failed provision and a destroyed database.
            await context.TrackResourceAsync(JobResourceKind.Volume, workload.DataVolumeName, false, ct)
                .ConfigureAwait(false);
            return;
        }

        await runtime.Volumes
            .CreateAsync(new VolumeSpec(workload.DataVolumeName, Labels(workload)), ct)
            .ConfigureAwait(false);

        await context.TrackResourceAsync(JobResourceKind.Volume, workload.DataVolumeName, true, ct)
            .ConfigureAwait(false);
    }

    private async Task<string> EnsureContainerAsync(
        IJobContext context,
        DatabaseWorkloadSnapshot workload,
        IDatabaseEngine engine,
        CancellationToken ct)
    {
        var existing = await runtime.Containers.FindAsync(workload.ContainerName, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            await context.TrackResourceAsync(JobResourceKind.Container, existing.Id, true, ct).ConfigureAwait(false);
            return existing.Id;
        }

        var spec = engine.BuildContainerSpec(
            workload.Spec,
            new ProvisionContext(
                workload.ContainerName,
                workload.NetworkName,
                workload.DataVolumeName,
                Labels(workload)));

        var containerId = await runtime.Containers.CreateAsync(spec, ct).ConfigureAwait(false);
        await context.TrackResourceAsync(JobResourceKind.Container, containerId, true, ct).ConfigureAwait(false);

        return containerId;
    }

    private async Task<bool> WaitForHealthAsync(IJobContext context, string containerId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(HealthTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var container = await runtime.Containers.FindAsync(containerId, ct).ConfigureAwait(false);

            if (container is null)
            {
                await context.LogStepAsync("health", "The container disappeared while starting.", ct)
                    .ConfigureAwait(false);
                return false;
            }

            switch (container.Health)
            {
                case ContainerHealth.Healthy:
                    await context.LogStepAsync("health", "Health check passed.", ct).ConfigureAwait(false);
                    return true;

                case ContainerHealth.Unhealthy when container.State == ContainerRunState.Exited:
                    await context.LogStepAsync(
                        "health",
                        $"The container exited with code {container.ExitCode}.", ct).ConfigureAwait(false);
                    return false;

                case ContainerHealth.None when container.State == ContainerRunState.Exited:
                    await context.LogStepAsync(
                        "health",
                        $"The container exited immediately with code {container.ExitCode}.", ct)
                        .ConfigureAwait(false);
                    return false;

                default:
                    break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        await context.LogStepAsync("health", "Timed out waiting for the health check.", ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Removes what this job created, newest first, and nothing else.
    /// </summary>
    /// <remarks>
    /// Reverse order matters: a network cannot be removed while a container is
    /// still attached to it, so containers go first. Resources flagged as
    /// pre-existing are skipped entirely.
    /// </remarks>
    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tracked = await context.GetTrackedResourcesAsync(ct).ConfigureAwait(false);

        foreach (var resource in tracked.Reverse())
        {
            if (!resource.CreatedByThisJob)
            {
                logger.LogInformation(
                    "Leaving {Kind} {Reference} in place; it existed before this job",
                    resource.Kind, resource.Reference);
                continue;
            }

            try
            {
                switch (resource.Kind)
                {
                    case JobResourceKind.Container:
                        await runtime.Containers.RemoveAsync(resource.Reference, force: true, ct)
                            .ConfigureAwait(false);
                        break;
                    case JobResourceKind.Volume:
                        await runtime.Volumes.RemoveAsync(resource.Reference, force: true, ct)
                            .ConfigureAwait(false);
                        break;
                    case JobResourceKind.Network:
                        await runtime.Networks.RemoveAsync(resource.Reference, ct).ConfigureAwait(false);
                        break;
                    default:
                        break;
                }

                await context.LogStepAsync(
                    "compensate", $"Removed {resource.Kind} {resource.Reference}.", ct).ConfigureAwait(false);
            }
            catch (ContainerRuntimeException ex)
            {
                // Recorded and carried on: one resource that will not go must not
                // stop the rest from being cleaned up.
                logger.LogError(ex, "Could not remove {Kind} {Reference}", resource.Kind, resource.Reference);
                await context.LogStepAsync(
                    "compensate",
                    $"Could not remove {resource.Kind} {resource.Reference}; it may need manual cleanup.", ct)
                    .ConfigureAwait(false);
            }
        }

        var payload = context.GetPayload<DatabaseProvisionPayload>();
        await store.SetStateAsync(payload.WorkloadId, Core.Workloads.DatabaseState.Failed.ToString(), ct)
            .ConfigureAwait(false);
    }

    private static Dictionary<string, string> Labels(DatabaseWorkloadSnapshot workload) => new(StringComparer.Ordinal)
    {
        [AirsideLabels.Managed] = AirsideLabels.True,
        [AirsideLabels.Kind] = AirsideLabels.KindDatabase,
        [AirsideLabels.WorkloadId] = workload.Id.ToString(),
        [AirsideLabels.Slug] = workload.Slug.Value,
        [AirsideLabels.Engine] = workload.Engine.ToString().ToLowerInvariant(),
    };
}
