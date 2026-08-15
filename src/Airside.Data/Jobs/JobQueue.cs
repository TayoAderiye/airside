using System.Text.Json;
using Airside.Core.Common;
using Airside.Core.Jobs;
using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Airside.Data.Jobs;

/// <summary>Raised when a job row is written, so the dispatcher can wake without polling.</summary>
public interface IJobSignal
{
    void Notify(Guid jobId);
}

/// <summary>
/// Durable job enqueue.
/// </summary>
/// <remarks>
/// The row is written first and the in-process signal raised second. If the
/// process dies between the two, the startup sweep finds the queued row and runs
/// it — whereas signalling first and writing second would lose the job entirely.
/// </remarks>
internal sealed class JobQueue(
    AirsideDbContext db,
    IJobSignal signal,
    TimeProvider timeProvider) : IJobQueue
{
    public async Task<Guid> EnqueueAsync<TPayload>(
        string jobType,
        TPayload payload,
        Guid? workloadId,
        Guid? triggeredByUserId,
        string idempotencyKey,
        CancellationToken ct)
    {
        var existing = await db.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // A double-clicked provision button returns the job that already
            // exists rather than creating a second container.
            return existing.Id;
        }

        var host = await db.Hosts.AsNoTracking().FirstAsync(ct).ConfigureAwait(false);

        var job = new Job
        {
            Type = jobType,
            HostId = host.Id,
            WorkloadId = workloadId,
            Status = JobStatus.Queued,
            PayloadJson = JsonSerializer.Serialize(payload),
            IdempotencyKey = idempotencyKey,
            TriggeredByUserId = triggeredByUserId,
            QueuedAt = timeProvider.GetUtcNow().UtcDateTime,
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        signal.Notify(job.Id);
        return job.Id;
    }

    public async Task<Result> RequestCancellationAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, ct).ConfigureAwait(false);

        if (job is null)
        {
            return new Error(ErrorCodes.JobNotFound, "No such job.");
        }

        if (job.IsTerminal)
        {
            return new Error(
                ErrorCodes.JobNotCancellable,
                "This job has already finished.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = job.Status.ToString(),
                });
        }

        job.CancellationRequested = true;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }
}
