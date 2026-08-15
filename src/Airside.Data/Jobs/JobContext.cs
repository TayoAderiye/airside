using System.Text.Json;
using Airside.Core.Jobs;
using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Airside.Data.Jobs;

/// <summary>Notified whenever a job's progress or step log changes, so hubs can push it.</summary>
public interface IJobProgressObserver
{
    Task OnJobUpdatedAsync(Guid jobId, CancellationToken ct);

    Task OnStepAppendedAsync(Guid jobId, int sequence, string name, string? message, CancellationToken ct);
}

/// <summary>
/// The handle a running job uses to report progress and to record what it created.
/// </summary>
/// <remarks>
/// Every write here is durable before the observer is notified. A step that has
/// been broadcast but not persisted disappears on restart, which is exactly when
/// somebody is reading the log to work out what happened.
/// </remarks>
public sealed class JobContext(
    AirsideDbContext db,
    IJobProgressObserver observer,
    TimeProvider timeProvider,
    Job job) : IJobContext
{
    public Guid JobId => job.Id;

    public Guid? WorkloadId => job.WorkloadId;

    public Guid? TriggeredByUserId => job.TriggeredByUserId;

    public TPayload GetPayload<TPayload>() =>
        JsonSerializer.Deserialize<TPayload>(job.PayloadJson)
        ?? throw new InvalidOperationException(
            $"Job {job.Id} of type {job.Type} has a payload that does not deserialise to {typeof(TPayload).Name}.");

    public async Task ReportProgressAsync(int percent, string currentStep, CancellationToken ct)
    {
        job.ProgressPercent = Math.Clamp(percent, 0, 100);
        job.CurrentStep = currentStep;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await observer.OnJobUpdatedAsync(job.Id, ct).ConfigureAwait(false);
    }

    public async Task LogStepAsync(string name, string message, CancellationToken ct)
    {
        var sequence = await db.JobSteps
            .Where(x => x.JobId == job.Id)
            .CountAsync(ct)
            .ConfigureAwait(false);

        db.JobSteps.Add(new JobStep
        {
            JobId = job.Id,
            Sequence = sequence,
            Name = name,
            Message = message,
            OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await observer.OnStepAppendedAsync(job.Id, sequence, name, message, ct).ConfigureAwait(false);
    }

    public async Task TrackResourceAsync(
        JobResourceKind kind,
        string reference,
        bool createdByThisJob,
        CancellationToken ct)
    {
        var alreadyTracked = await db.JobResources
            .AnyAsync(x => x.JobId == job.Id && x.Kind == kind && x.Reference == reference, ct)
            .ConfigureAwait(false);

        if (alreadyTracked)
        {
            // Handlers are idempotent, so a re-run re-tracks what it already
            // created. Recording it twice would make compensation try to remove
            // the same resource twice and log a spurious failure.
            return;
        }

        db.JobResources.Add(new JobResource
        {
            JobId = job.Id,
            Kind = kind,
            Reference = reference,
            CreatedByThisJob = createdByThisJob,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrackedResource>> GetTrackedResourcesAsync(CancellationToken ct)
    {
        var rows = await db.JobResources
            .AsNoTracking()
            .Where(x => x.JobId == job.Id && x.CompensatedAt == null)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(x => new TrackedResource(
            x.Kind, x.Reference, x.CreatedByThisJob, new DateTimeOffset(x.CreatedAt, TimeSpan.Zero)))];
    }
}
