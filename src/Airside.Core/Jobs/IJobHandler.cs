using Airside.Core.Common;

namespace Airside.Core.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Compensating,
    Compensated,
}

/// <summary>Resource kinds a job can create, and therefore must be able to unwind.</summary>
public enum JobResourceKind
{
    Container,
    Volume,
    Network,
    Image,
    ProxyRoute,
}

/// <summary>
/// Handles one job type.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CompensateAsync"/> is part of the contract rather than a
/// convention, because "a deployment that fails at the health-check step must not
/// leave an orphaned container, volume, network, or proxy route behind" is not
/// something a reviewer can reliably verify by reading. A handler that cannot
/// unwind itself does not compile.
/// </para>
/// <para>
/// Handlers must be idempotent by workload id. Re-running a provision finds the
/// existing container by label and converges rather than creating a second one —
/// the recovery sweep after a restart depends on this.
/// </para>
/// </remarks>
public interface IJobHandler
{
    /// <summary>Matches <c>Job.Type</c>, e.g. <c>database.provision</c>.</summary>
    string JobType { get; }

    Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct);

    /// <summary>
    /// Unwinds whatever <see cref="ExecuteAsync"/> created, walking
    /// <see cref="IJobContext.TrackResourceAsync"/> records in reverse. Must
    /// tolerate being called after a partial execution, and after a process
    /// restart that lost all in-memory state.
    /// </summary>
    Task CompensateAsync(IJobContext context, CancellationToken ct);
}

/// <summary>What a handler is given: its payload, and the means to report and to track.</summary>
public interface IJobContext
{
    Guid JobId { get; }

    Guid? WorkloadId { get; }

    Guid? TriggeredByUserId { get; }

    /// <summary>Deserialises the typed payload for this job type.</summary>
    TPayload GetPayload<TPayload>();

    Task ReportProgressAsync(int percent, string currentStep, CancellationToken ct);

    /// <summary>
    /// Appends to the step log.
    /// </summary>
    /// <remarks>
    /// The sequence number doubles as the resume id on the live stream, so a
    /// client that reconnects continues from the step it last saw rather than
    /// replaying the whole log or missing the middle of it.
    /// </remarks>
    Task LogStepAsync(string name, string message, CancellationToken ct);

    /// <summary>
    /// Records a resource so compensation can remove it.
    /// </summary>
    /// <param name="createdByThisJob">
    /// False when the resource already existed. This is what stops a retry from
    /// deleting a volume that predates the job — the difference between a failed
    /// provision and a destroyed database.
    /// </param>
    Task TrackResourceAsync(
        JobResourceKind kind,
        string reference,
        bool createdByThisJob,
        CancellationToken ct);

    /// <summary>Resources tracked so far, oldest first. Compensation walks these in reverse.</summary>
    Task<IReadOnlyList<TrackedResource>> GetTrackedResourcesAsync(CancellationToken ct);
}

public sealed record TrackedResource(
    JobResourceKind Kind,
    string Reference,
    bool CreatedByThisJob,
    DateTimeOffset TrackedAt);

/// <summary>Enqueues work. Every long-running operation goes through here rather than blocking a request.</summary>
public interface IJobQueue
{
    /// <summary>
    /// Enqueues a job and returns its id immediately.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Deduplicates against an in-flight job. Re-submitting the same provision
    /// returns the existing job rather than starting a second one.
    /// </param>
    Task<Guid> EnqueueAsync<TPayload>(
        string jobType,
        TPayload payload,
        Guid? workloadId,
        Guid? triggeredByUserId,
        string idempotencyKey,
        CancellationToken ct);

    Task<Result> RequestCancellationAsync(Guid jobId, CancellationToken ct);
}
