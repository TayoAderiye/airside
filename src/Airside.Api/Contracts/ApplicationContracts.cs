using Airside.Data.Entities;

namespace Airside.Api.Contracts;

public sealed record CreateApplicationRequest
{
    public required string Slug { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public required long CpuNanos { get; init; }

    public required long MemoryBytes { get; init; }

    public long StorageBytes { get; init; }

    public bool AutoRestart { get; init; } = true;

    /// <summary>The port the application listens on inside its container.</summary>
    public required int ContainerPort { get; init; }

    /// <summary><c>image</c>, <c>git</c>, or <c>dockerfile</c>. Compose is out of scope.</summary>
    public required string SourceKind { get; init; }

    public string? ImageRef { get; init; }

    public string? GitRepositoryUrl { get; init; }

    public string? GitBranch { get; init; }

    /// <summary>Relative to the repository root. Rejected if it escapes the build context.</summary>
    public string? DockerfilePath { get; init; }

    public string? DockerfileContent { get; init; }

    /// <summary>
    /// Required, with no "none" option.
    /// </summary>
    /// <remarks>
    /// Zero-downtime deployment is start-new, poll-health, swap, stop-old. Without
    /// a health check that degrades to waiting a few seconds and hoping, and the
    /// API should not let you ask for a guarantee it cannot keep.
    /// </remarks>
    public required HealthCheckRequest HealthCheck { get; init; }
}

/// <param name="Command">An argument vector, never a command line. There is no shell.</param>
public sealed record HealthCheckRequest(
    string Kind,
    string? Path,
    int? ExpectedStatus,
    IReadOnlyList<string>? Command,
    int IntervalSeconds = 10,
    int TimeoutSeconds = 5,
    int Retries = 3);

public sealed record DeployRequest(string? Branch, string? CommitSha, string? ImageRef);

public sealed record DeleteApplicationRequest(string ConfirmSlug, bool DeleteVolumes);

public sealed record ApplicationSummaryDto(
    Guid Id,
    string Slug,
    string DisplayName,
    string State,
    DateTimeOffset StateChangedAt,
    string SourceKind,
    long CpuNanos,
    long MemoryBytes,
    int ContainerPort,
    Guid? CurrentDeploymentId,
    Guid? ActiveJobId,
    bool IsSystem)
{
    public static ApplicationSummaryDto From(Application a)
    {
        ArgumentNullException.ThrowIfNull(a);

        return new ApplicationSummaryDto(
            a.Id,
            a.Slug,
            a.DisplayName,
            DatabaseSummaryDto.Camel(a.State),
            new DateTimeOffset(a.StateChangedAt, TimeSpan.Zero),
            a.SourceKind.ToString().ToLowerInvariant(),
            a.CpuLimitNanos,
            a.MemoryLimitBytes,
            a.ContainerPort,
            a.CurrentDeploymentId,
            a.ActiveJobId,
            IsSystem: false);
    }
}

public sealed record DeploymentDto(
    Guid Id,
    Guid ApplicationId,
    int Number,
    string Status,
    string TriggerKind,
    string? CommitSha,
    string? CommitMessage,
    string? Branch,
    string? ImageRef,
    string? ImageDigest,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs,
    bool IsCurrent,
    Guid? RolledBackFromDeploymentId,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<WarningDto> Warnings)
{
    public static DeploymentDto From(Deployment d, bool canRollBack)
    {
        ArgumentNullException.ThrowIfNull(d);

        var warnings = new List<WarningDto>();

        if (!canRollBack)
        {
            warnings.Add(new WarningDto(
                "deployment.no_previous_image",
                "There is no earlier successful deployment with a retained image, so this application "
                + "cannot be rolled back yet."));
        }

        return new DeploymentDto(
            d.Id,
            d.ApplicationId,
            d.Number,
            d.Status.ToString().ToLowerInvariant(),
            d.TriggerKind.ToString().ToLowerInvariant(),
            d.CommitSha,
            d.CommitMessage,
            d.Branch,
            d.ImageRef,
            d.ImageDigest,
            new DateTimeOffset(d.StartedAt, TimeSpan.Zero),
            d.CompletedAt is null ? null : new DateTimeOffset(d.CompletedAt.Value, TimeSpan.Zero),
            d.DurationMs,
            d.IsCurrent,
            d.RolledBackFromDeploymentId,
            d.ErrorCode,
            d.ErrorMessage,
            warnings);
    }
}

/// <param name="Editable">
/// False for an attachment-injected entry. Editing one would be overwritten at
/// the next deploy, because injected values are rendered from the attachment and
/// the live credential rather than stored.
/// </param>
public sealed record EnvironmentEntryDto(
    string Key,
    string Value,
    bool IsSecret,
    string Source,
    Guid? SourceAttachmentId,
    bool Editable,
    string? RevealUrl,
    DateTimeOffset? UpdatedAt);

public sealed record SetEnvironmentRequest(string Value, bool IsSecret);

public sealed record AttachDatabaseRequest(Guid DatabaseId, string? EnvKeyPrefix);

public sealed record AttachmentDto(
    Guid Id,
    Guid DatabaseId,
    string DatabaseSlug,
    string Engine,
    string EnvKeyPrefix,
    IReadOnlyList<string> InjectedKeys,
    DateTimeOffset AttachedAt);
