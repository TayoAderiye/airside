using Airside.Core.Hosting;
using Airside.Core.Jobs;
using Airside.Data.Entities;

namespace Airside.Api.Contracts;

/// <summary>
/// The body every 202 returns.
/// </summary>
/// <remarks>
/// Carries both a poll URL and a stream URL. A client opens an EventSource on
/// <c>eventsUrl</c> for live progress and falls back to polling
/// <c>statusUrl</c>; both are always valid, so an intermediary that mangles
/// streaming degrades the experience rather than the product.
/// </remarks>
public sealed record JobAccepted(
    Guid JobId,
    string JobType,
    Guid? WorkloadId,
    string StatusUrl,
    string EventsUrl)
{
    public static JobAccepted From(Guid jobId, string jobType, Guid? workloadId) => new(
        jobId,
        jobType,
        workloadId,
        $"/api/v1/jobs/{jobId}",
        $"/api/v1/jobs/{jobId}/events");
}

public sealed record JobStepDto(int Sequence, string Name, string? Message, DateTimeOffset OccurredAt);

public sealed record JobDto(
    Guid Id,
    string Type,
    string Status,
    int ProgressPercent,
    string? CurrentStep,
    Guid? WorkloadId,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<JobStepDto> Steps)
{
    public static JobDto From(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobDto(
            job.Id,
            job.Type,
            Camel(job.Status),
            job.ProgressPercent,
            job.CurrentStep,
            job.WorkloadId,
            job.QueuedAt,
            job.StartedAt,
            job.CompletedAt,
            job.ErrorCode,
            job.ErrorMessage,
            [.. job.Steps.OrderBy(s => s.Sequence)
                .Select(s => new JobStepDto(s.Sequence, s.Name, s.Message, s.OccurredAt))]);
    }

    // ErrorDetail is deliberately absent. It holds a stack trace, which is
    // operator-facing log material, not something to hand to a browser.
    private static string Camel(JobStatus status) =>
        char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..];
}

public sealed record LogLineDto(DateTimeOffset Timestamp, string Stream, string Text);

/// <param name="CpuNanos">
/// Null until a container has been sampled twice. Docker's one-shot stats call
/// carries no previous CPU reading, so the first sample can only yield a
/// meaningless 0% — the field is null rather than a plausible lie.
/// </param>
public sealed record MetricSampleDto(
    DateTimeOffset SampledAt,
    long? CpuNanos,
    long MemoryBytes,
    long MemoryLimitBytes);

public sealed record ResourceTripleDto(long CpuNanos, long MemoryBytes, long StorageBytes)
{
    public static ResourceTripleDto From(ResourceTriple triple)
    {
        ArgumentNullException.ThrowIfNull(triple);
        return new ResourceTripleDto(triple.CpuNanos, triple.MemoryBytes, triple.StorageBytes);
    }
}

public sealed record WarningDto(string Code, string Message, IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>
/// Host capacity, allocation, and usage as three separate numbers.
/// </summary>
/// <remarks>
/// They are never merged into one "percent used" figure here, because a host at
/// 40% memory usage may still be unable to accept another workload — allocation,
/// not usage, is what admission depends on, and a dashboard that shows only usage
/// makes a full host look idle.
/// </remarks>
public sealed record HostDto(
    Guid Id,
    string Name,
    ResourceTripleDto Capacity,
    ResourceTripleDto Reserve,
    ResourceTripleDto Allocated,
    ResourceTripleDto? Used,
    ResourceTripleDto Available,
    string StorageEnforcement,
    string? DockerApiVersion,
    string? KernelVersion,
    string? OperatingSystem,
    DateTimeOffset? LastDiscoveredAt,
    IReadOnlyList<WarningDto> Warnings);

public sealed record CurrentUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool MfaEnabled,
    bool MustChangePassword);

public sealed record LoginRequest(string Email, string Password, string? TotpCode);

public sealed record SetupStatusDto(
    bool SetupCompleted,
    string StoreProvider,
    string Version,
    bool AwaitingDomain);

public sealed record SetupCompleteRequest(
    string SetupToken,
    string Email,
    string Password,
    string DisplayName,
    string InstanceName);

public sealed record AuditEventDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? UserId,
    string? UserEmail,
    string Action,
    string? ResourceKind,
    Guid? ResourceId,
    string? ResourceSlug,
    string Result,
    string? IpAddress,
    string? CorrelationId);

public sealed record CursorResult<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    IReadOnlyList<string> Roles,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt);

public sealed record RoleDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    bool IsSystem,
    IReadOnlyList<string> Permissions);

public sealed record PermissionDto(string Code, string? Description, bool IsObsolete);

public sealed record SystemInfoDto(
    string Version,
    string? CurrentImageTag,
    string StoreProvider,
    string InstanceName,
    bool RuntimeAvailable,
    DateTimeOffset StartedAt);
