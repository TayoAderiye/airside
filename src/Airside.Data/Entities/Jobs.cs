using Airside.Core.Jobs;

namespace Airside.Data.Entities;

/// <summary>
/// A long-running operation.
/// </summary>
/// <remarks>
/// Jobs are persisted rows, not just entries in an in-memory channel. The channel
/// is the dispatcher; it is not the store. An in-memory queue loses its contents
/// when the API restarts — and self-update restarts the API by design — so a
/// provision in flight during an update would vanish, leaving an orphaned
/// container that reconciliation later reports as unexplained drift.
/// </remarks>
public class Job : Entity
{
    public string Type { get; set; } = string.Empty;

    public Guid HostId { get; set; }

    public Guid? WorkloadId { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public int ProgressPercent { get; set; }

    public string? CurrentStep { get; set; }

    /// <summary>The typed payload, serialised. Deserialised by the handler for its own job type.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// Deduplicates against an in-flight job. A double-clicked provision button
    /// returns the existing job rather than creating a second container.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public int AttemptCount { get; set; }

    /// <summary>Identifies the dispatcher holding this job, so a dead one can be detected.</summary>
    public string? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresAt { get; set; }

    public bool CancellationRequested { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ErrorDetail { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public Guid? TriggeredByUserId { get; set; }

    public string? CorrelationId { get; set; }

    public ICollection<JobStep> Steps { get; } = new List<JobStep>();

    public ICollection<JobResource> Resources { get; } = new List<JobResource>();

    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Failed
        or JobStatus.Cancelled or JobStatus.Compensated;
}

/// <summary>
/// One entry in a job's append-only step log.
/// </summary>
/// <remarks>
/// Verbose output — build logs, pg_dump stderr — deliberately does not land here.
/// It streams over SignalR and, where worth keeping, goes to a deployment log. A
/// job table carrying megabytes of container output makes every job list query
/// slow, and job lists are on the busiest screen in the product.
/// </remarks>
public class JobStep : Entity
{
    public Guid JobId { get; set; }

    public Job Job { get; set; } = null!;

    public int Sequence { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Message { get; set; }

    public DateTime OccurredAt { get; set; }
}

/// <summary>
/// A resource a job created, recorded so compensation can remove it.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns "a deployment that fails at the health-check step must not
/// leave an orphaned container, volume, network, or proxy route behind" from an
/// aspiration into a query. Each resource is written here as it is created, and
/// compensation walks the rows in reverse.
/// </para>
/// <para>
/// Because the rows are durable, a job whose process was killed outright is still
/// recoverable — the startup sweep has an exact list rather than having to infer
/// one from the state of the world.
/// </para>
/// </remarks>
public class JobResource : Entity
{
    public Guid JobId { get; set; }

    public Job Job { get; set; } = null!;

    public JobResourceKind Kind { get; set; }

    public string Reference { get; set; } = string.Empty;

    /// <summary>
    /// False when the resource already existed. This single flag is the difference
    /// between a failed provision cleaning up after itself and a retry destroying
    /// a database that was there before it started.
    /// </summary>
    public bool CreatedByThisJob { get; set; }

    public DateTime? CompensatedAt { get; set; }
}
