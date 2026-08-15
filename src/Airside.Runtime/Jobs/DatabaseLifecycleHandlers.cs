using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Jobs;
using Airside.Core.Workloads;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Jobs;

/// <summary>
/// Start, stop, and restart.
/// </summary>
/// <remarks>
/// These create nothing, so compensation has nothing to unwind — but the state
/// must still be put back somewhere truthful rather than left mid-transition,
/// which is what the compensate path does here.
/// </remarks>
public sealed class DatabaseLifecycleHandler(
    IContainerRuntime runtime,
    IDatabaseWorkloadStore store,
    string jobType,
    DatabaseState targetState) : IJobHandler
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public string JobType => jobType;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<DatabaseLifecyclePayload>();
        var workload = await store.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);

        if (workload?.ContainerId is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The database has no container to act on.");
        }

        await context.ReportProgressAsync(30, jobType, ct).ConfigureAwait(false);

        switch (jobType)
        {
            case DatabaseJobTypes.Start:
                await runtime.Containers.StartAsync(workload.ContainerId, ct).ConfigureAwait(false);
                break;
            case DatabaseJobTypes.Stop:
                // A graceful stop, so the engine gets to flush and close cleanly.
                // Docker sends SIGKILL after the timeout regardless.
                await runtime.Containers.StopAsync(workload.ContainerId, StopTimeout, ct).ConfigureAwait(false);
                break;
            case DatabaseJobTypes.Restart:
                await runtime.Containers.RestartAsync(workload.ContainerId, StopTimeout, ct).ConfigureAwait(false);
                break;
            default:
                return new Error("job.no_handler", $"'{jobType}' is not a lifecycle operation.");
        }

        await store.SetStateAsync(workload.Id, targetState.ToString(), ct).ConfigureAwait(false);
        await context.ReportProgressAsync(100, targetState.ToString(), ct).ConfigureAwait(false);

        return Result.Ok();
    }

    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Nothing was created, so nothing is removed. What matters is that the
        // workload does not stay in Restarting forever because a stop failed
        // halfway — the real container state is the truth, so read it back.
        var payload = context.GetPayload<DatabaseLifecyclePayload>();
        var workload = await store.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);

        if (workload?.ContainerId is null)
        {
            await store.SetStateAsync(payload.WorkloadId, nameof(DatabaseState.Failed), ct)
                .ConfigureAwait(false);
            return;
        }

        var container = await runtime.Containers.FindAsync(workload.ContainerId, ct).ConfigureAwait(false);

        var observed = container?.State switch
        {
            ContainerRunState.Running => DatabaseState.Running,
            ContainerRunState.Exited or ContainerRunState.Created => DatabaseState.Stopped,
            _ => DatabaseState.Failed,
        };

        await store.SetStateAsync(payload.WorkloadId, observed.ToString(), ct).ConfigureAwait(false);
        await context.LogStepAsync("compensate", $"State reconciled to {observed} from the container.", ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Applies new CPU and memory limits.
/// </summary>
/// <remarks>
/// Docker can update limits on a running container, but a database that has
/// already sized its buffers to the old limit will not notice — Postgres's
/// shared_buffers and Redis's maxmemory are read at start. So the workload is
/// restarted, and the API tells the user that up front rather than leaving them
/// with a resize that silently did nothing.
/// </remarks>
public sealed class DatabaseResizeHandler(
    IContainerRuntime runtime,
    IDatabaseWorkloadStore store) : IJobHandler
{
    public string JobType => DatabaseJobTypes.Resize;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<DatabaseResizePayload>();
        var workload = await store.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);

        if (workload?.ContainerId is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The database has no container to resize.");
        }

        // The admission check already passed in the service layer, inside the same
        // transaction that wrote the new limits. This handler applies them.
        await context.ReportProgressAsync(40, "Applying limits", ct).ConfigureAwait(false);
        await store.RecordLimitsAsync(
            workload.Id, payload.CpuNanos, payload.MemoryBytes, payload.StorageBytes, ct).ConfigureAwait(false);

        await context.ReportProgressAsync(70, "Restarting", ct).ConfigureAwait(false);
        await runtime.Containers
            .RestartAsync(workload.ContainerId, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);

        await store.SetStateAsync(workload.Id, DatabaseState.Running.ToString(), ct).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Running", ct).ConfigureAwait(false);

        return Result.Ok();
    }

    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.GetPayload<DatabaseResizePayload>();
        await store.SetStateAsync(payload.WorkloadId, DatabaseState.Failed.ToString(), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Removes a database.
/// </summary>
/// <remarks>
/// The volume is removed only when the admin explicitly opted in. Otherwise it is
/// kept and marked orphaned: it keeps counting against allocated storage and
/// appears on the reclaim screen, so the disk it occupies is visible rather than
/// mysteriously missing.
/// </remarks>
public sealed class DatabaseDeleteHandler(
    IContainerRuntime runtime,
    IDatabaseWorkloadStore store,
    ILogger<DatabaseDeleteHandler> logger) : IJobHandler
{
    public string JobType => DatabaseJobTypes.Delete;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<DatabaseDeletePayload>();
        var workload = await store.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);

        if (workload is null)
        {
            return Result.Ok();
        }

        if (workload.ContainerId is not null)
        {
            await context.ReportProgressAsync(20, "Stopping", ct).ConfigureAwait(false);
            await runtime.Containers.RemoveAsync(workload.ContainerId, force: true, ct).ConfigureAwait(false);
            await context.LogStepAsync("container", "Container removed.", ct).ConfigureAwait(false);
        }

        await context.ReportProgressAsync(50, "Removing network", ct).ConfigureAwait(false);
        await runtime.Networks.RemoveAsync(workload.NetworkName, ct).ConfigureAwait(false);

        if (payload.DeleteVolume)
        {
            await context.ReportProgressAsync(75, "Deleting data", ct).ConfigureAwait(false);
            await runtime.Volumes.RemoveAsync(workload.DataVolumeName, force: false, ct).ConfigureAwait(false);

            logger.LogWarning(
                "Volume {VolumeName} for database {Slug} was deleted at the operator's explicit request",
                workload.DataVolumeName, workload.Slug.Value);

            await context.LogStepAsync("volume", $"Deleted volume {workload.DataVolumeName}.", ct)
                .ConfigureAwait(false);
        }
        else
        {
            await context.LogStepAsync(
                "volume",
                $"Kept volume {workload.DataVolumeName}. It still holds the data and still counts "
                + "against allocated storage; reclaim it from the Volumes screen.", ct).ConfigureAwait(false);
        }

        await store.RecordDeletedAsync(workload.Id, payload.DeleteVolume, ct).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Deleted", ct).ConfigureAwait(false);

        return Result.Ok();
    }

    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A half-finished delete cannot be undone — the container is already gone.
        // The workload goes to Failed so it stays visible and can be retried,
        // rather than vanishing and leaving its volume unaccounted for.
        var payload = context.GetPayload<DatabaseDeletePayload>();
        await store.SetStateAsync(payload.WorkloadId, DatabaseState.Failed.ToString(), ct).ConfigureAwait(false);
        await context.LogStepAsync(
            "compensate",
            "Deletion did not complete. The database is marked failed and can be deleted again.", ct)
            .ConfigureAwait(false);
    }
}
