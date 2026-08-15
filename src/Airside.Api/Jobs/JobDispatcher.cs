using System.Threading.Channels;
using Airside.Core.Common;
using Airside.Core.Jobs;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Data.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Jobs;

/// <summary>In-process wake-up for the dispatcher. The store is the queue; this is only the doorbell.</summary>
public sealed class JobSignal : IJobSignal
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Notify(Guid jobId) => _channel.Writer.TryWrite(jobId);
}

public interface IJobHandlerRegistry
{
    IJobHandler? Find(string jobType);
}

public sealed class JobHandlerRegistry(IEnumerable<IJobHandler> handlers) : IJobHandlerRegistry
{
    private readonly Dictionary<string, IJobHandler> _handlers =
        handlers.ToDictionary(h => h.JobType, StringComparer.Ordinal);

    public IJobHandler? Find(string jobType) => _handlers.GetValueOrDefault(jobType);
}

/// <summary>
/// Runs queued jobs, one at a time per workload.
/// </summary>
/// <remarks>
/// <para>
/// The store is the queue. <see cref="JobSignal"/> only saves the dispatcher from
/// polling — every path also re-checks the database, so a job enqueued while the
/// process was down still runs.
/// </para>
/// <para>
/// A single reader loop is deliberate. Airside is one instance managing one host,
/// and serialising the dispatcher removes an entire class of races — two handlers
/// touching the same container, two provisions passing the same admission check —
/// in exchange for throughput that a single-host control plane does not need.
/// </para>
/// </remarks>
public sealed class JobDispatcherService(
    JobSignal signal,
    IServiceScopeFactory scopeFactory,
    IJobProgressObserver observer,
    TimeProvider timeProvider,
    ILogger<JobDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverOrphanedJobsAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var ranSomething = await RunNextAsync(stoppingToken).ConfigureAwait(false);

            if (ranSomething)
            {
                continue;
            }

            // Wait for a signal, but wake periodically anyway. A missed
            // notification must cost fifteen seconds of latency, not a stuck queue.
            using var idle = new CancellationTokenSource(IdlePoll);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, idle.Token);

            try
            {
                await signal.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Idle timeout or shutdown; the loop condition decides which.
            }
        }
    }

    /// <summary>
    /// Deals with jobs left Running by a process that died.
    /// </summary>
    /// <remarks>
    /// This is why jobs are rows rather than channel entries. Self-update restarts
    /// the API by design, so a provision in flight during an update would otherwise
    /// vanish — leaving a container, a volume, and a network that reconciliation
    /// later reports as unexplained drift with nobody able to say where it came
    /// from. Here they are moved to Compensating and unwound from their own
    /// recorded resource list.
    /// </remarks>
    private async Task RecoverOrphanedJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var orphaned = await db.Jobs
            .Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Compensating)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var job in orphaned)
        {
            logger.LogWarning(
                "Job {JobId} ({JobType}) was left {Status} by a previous process; compensating",
                job.Id, job.Type, job.Status);

            job.Status = JobStatus.Compensating;
            job.LeaseOwner = _leaseOwner;
            job.LeaseExpiresAt = now.Add(LeaseDuration);
        }

        if (orphaned.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            orphaned.ForEach(j => signal.Notify(j.Id));
        }
    }

    private async Task<bool> RunNextAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IJobHandlerRegistry>();

        var job = await ClaimNextAsync(db, ct).ConfigureAwait(false);

        if (job is null)
        {
            return false;
        }

        var handler = registry.Find(job.Type);

        if (handler is null)
        {
            await FailAsync(db, job, new Error("job.no_handler",
                $"No handler is registered for job type '{job.Type}'."), ct).ConfigureAwait(false);
            return true;
        }

        var context = new JobContext(db, observer, timeProvider, job);

        if (job.Status == JobStatus.Compensating)
        {
            await CompensateAsync(db, job, handler, context, ct).ConfigureAwait(false);
            return true;
        }

        try
        {
            var result = await handler.ExecuteAsync(context, ct).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                job.Status = JobStatus.Succeeded;
                job.ProgressPercent = 100;
                job.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
                ReleaseLease(job);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await observer.OnJobUpdatedAsync(job.Id, ct).ConfigureAwait(false);
                return true;
            }

            job.ErrorCode = result.Failure!.Code;
            job.ErrorMessage = result.Failure.Message;
        }
#pragma warning disable CA1031 // A handler may throw anything; the dispatcher must survive it and compensate.
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} ({JobType}) threw", job.Id, job.Type);
            job.ErrorCode = "job.unhandled_exception";
            job.ErrorMessage = "The operation failed unexpectedly.";
            job.ErrorDetail = ex.ToString();
        }
#pragma warning restore CA1031

        job.Status = JobStatus.Compensating;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await CompensateAsync(db, job, handler, context, ct).ConfigureAwait(false);
        return true;
    }

    private async Task CompensateAsync(
        AirsideDbContext db,
        Job job,
        IJobHandler handler,
        IJobContext context,
        CancellationToken ct)
    {
        try
        {
            await handler.CompensateAsync(context, ct).ConfigureAwait(false);
            job.Status = JobStatus.Compensated;
        }
#pragma warning disable CA1031 // A failed cleanup must be recorded, not rethrown into the dispatcher loop.
        catch (Exception ex)
        {
            logger.LogError(ex, "Compensation for job {JobId} failed; resources may be orphaned", job.Id);
            job.Status = JobStatus.Failed;
            job.ErrorDetail = $"{job.ErrorDetail}\nCompensation also failed: {ex.Message}";
        }
#pragma warning restore CA1031

        job.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        ReleaseLease(job);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await observer.OnJobUpdatedAsync(job.Id, ct).ConfigureAwait(false);
    }

    private async Task FailAsync(AirsideDbContext db, Job job, Error error, CancellationToken ct)
    {
        job.Status = JobStatus.Failed;
        job.ErrorCode = error.Code;
        job.ErrorMessage = error.Message;
        job.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        ReleaseLease(job);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await observer.OnJobUpdatedAsync(job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the oldest queued job whose workload is free.
    /// </summary>
    /// <remarks>
    /// A second job for a busy workload stays queued rather than failing: a resize
    /// arriving during a backup would corrupt both, and rejecting it outright
    /// would make the UI's "restart" button fail for reasons the user cannot see.
    /// </remarks>
    private async Task<Job?> ClaimNextAsync(AirsideDbContext db, CancellationToken ct)
    {
        var busyWorkloads = await db.Jobs
            .Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Compensating)
            .Select(j => j.WorkloadId)
            .Where(id => id != null)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var job = await db.Jobs
            .Where(j => j.Status == JobStatus.Queued)
            .Where(j => j.WorkloadId == null || !busyWorkloads.Contains(j.WorkloadId))
            .OrderBy(j => j.QueuedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (job is null)
        {
            return null;
        }

        if (job.CancellationRequested)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return null;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = timeProvider.GetUtcNow().UtcDateTime;
        job.AttemptCount++;
        job.LeaseOwner = _leaseOwner;
        job.LeaseExpiresAt = timeProvider.GetUtcNow().UtcDateTime.Add(LeaseDuration);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await observer.OnJobUpdatedAsync(job.Id, ct).ConfigureAwait(false);

        return job;
    }

    private static void ReleaseLease(Job job)
    {
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
    }
}
