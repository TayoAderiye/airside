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

public sealed record AddDomainRequest(string Hostname);

/// <param name="ExpiresInDays">
/// Null when nothing is being served yet. Negative when it has already expired,
/// which is worth showing rather than clamping to zero.
/// </param>
public sealed record DomainDto(
    Guid Id,
    Guid ApplicationId,
    string Hostname,
    bool IsPrimary,
    string State,
    string? CertificateIssuer,
    DateTimeOffset? CertificateNotAfter,
    int? ExpiresInDays,
    bool CertificateAutoRenew,
    DateTimeOffset? LastCheckedAt,
    string? ErrorCode,
    IReadOnlyList<WarningDto> Warnings)
{
    public static DomainDto From(Domain d, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(d);

        var expiresIn = d.CertificateNotAfter is { } notAfter
            ? (int)Math.Floor((notAfter - now).TotalDays)
            : (int?)null;

        var warnings = new List<WarningDto>();

        if (d.State == DomainState.Pending)
        {
            warnings.Add(new WarningDto(
                "domain.awaiting_certificate",
                "The route is registered but no certificate is being served yet. Point this hostname's DNS "
                + "at this host; Let's Encrypt cannot issue until it resolves here."));
        }

        // Let's Encrypt renews at 30 days remaining, so anything under 14 means
        // renewal has been failing for a fortnight and nobody noticed.
        if (expiresIn is <= 14)
        {
            warnings.Add(new WarningDto(
                "domain.certificate_expiring",
                $"The certificate expires in {expiresIn} days and automatic renewal appears not to have "
                + "run. Check that the hostname still resolves here and that port 80 is reachable.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["expiresInDays"] = expiresIn }));
        }

        return new DomainDto(
            d.Id,
            d.ApplicationId,
            d.Hostname,
            d.IsPrimary,
            d.State.ToString().ToLowerInvariant(),
            d.CertificateIssuer,
            d.CertificateNotAfter is null ? null : new DateTimeOffset(d.CertificateNotAfter.Value, TimeSpan.Zero),
            expiresIn,
            d.CertificateAutoRenew,
            d.LastCertificateCheckAt is null
                ? null
                : new DateTimeOffset(d.LastCertificateCheckAt.Value, TimeSpan.Zero),
            d.ErrorCode,
            warnings);
    }
}

public sealed record CertificateDto(
    string Hostname,
    string? Issuer,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    bool AutoRenew,
    bool IsValid,
    string? Detail);
