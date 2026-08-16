using Airside.Data.Entities;

namespace Airside.Api.Contracts;

public sealed record DatabaseCapabilitiesDto(
    bool SupportsDatabaseName,
    bool SupportsUserAccounts,
    bool SupportsLogicalBackup,
    bool SupportsSnapshotBackup,
    bool RequiresStopForRestore,
    bool RequiresMaxMemory,
    string QueryDialect,
    string DefaultEnvKeyPrefix);

/// <param name="Note">
/// Inline guidance for this choice, or null when there is nothing worth saying.
/// </param>
/// <remarks>
/// The note is deliberately absent on the default. A warning attached to the
/// path most users take is noise, and it teaches them that these messages can be
/// dismissed without reading — which is exactly the habit you do not want when a
/// real one appears.
/// </remarks>
public sealed record ImageVariantDto(string Value, string DisplayName, bool IsDefault, string? Note);

/// <param name="Variants">
/// What the engine actually publishes. A single entry means the UI renders no
/// control at all — there is no choice to present.
/// </param>
public sealed record DatabaseEngineDto(
    string Kind,
    string DisplayName,
    IReadOnlyList<string> SupportedVersions,
    string DefaultVersion,
    int DefaultPort,
    DatabaseCapabilitiesDto Capabilities,
    IReadOnlyList<string>? MaxMemoryPolicies,
    IReadOnlyList<string> InjectedEnvKeys,
    IReadOnlyList<ImageVariantDto> Variants);

public sealed record CreateDatabaseRequest
{
    public required string Slug { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public required string Engine { get; init; }

    public required string Version { get; init; }

    /// <summary>
    /// <c>alpine</c> or <c>debian</c>. Omit to take the engine's own default.
    /// </summary>
    /// <remarks>
    /// Fixed once the database exists. Rejected outright for an engine that does
    /// not publish the variant, and rejected alongside <c>customImage</c>, which
    /// bypasses variant resolution entirely.
    /// </remarks>
    public string? ImageVariant { get; init; }

    /// <summary>
    /// An explicit image, used exactly as given.
    /// </summary>
    /// <remarks>
    /// The way to run a Postgres carrying pgvector or PostGIS. Airside cannot
    /// reason about the contents, so the workload is flagged
    /// <c>usesCustomImage</c> and variant and version guidance stop applying.
    /// </remarks>
    public string? CustomImage { get; init; }

    public required long CpuNanos { get; init; }

    public required long MemoryBytes { get; init; }

    public required long StorageBytes { get; init; }

    public bool AutoRestart { get; init; } = true;

    /// <summary>Null means not published to the host, which is the default.</summary>
    public int? PublishedPort { get; init; }

    /// <summary>
    /// Defaults to loopback. <c>0.0.0.0</c> is accepted but returns
    /// <c>database.published_publicly</c>, and the UI must confirm it separately.
    /// </summary>
    public string? PublishBindAddress { get; init; }

    public string? DatabaseName { get; init; }

    public string? Username { get; init; }

    /// <summary>Omit to have one generated. Never echoed back in any response.</summary>
    public string? Password { get; init; }

    public long? MaxMemoryBytes { get; init; }

    public string? MaxMemoryPolicy { get; init; }

    public bool? AofEnabled { get; init; }

    public bool BackupEnabled { get; init; } = true;

    public string? BackupCron { get; init; }

    public int? BackupRetentionCount { get; init; }

    public int? BackupRetentionDays { get; init; }
}

public sealed record ResizeDatabaseRequest(long CpuNanos, long MemoryBytes, long StorageBytes);

/// <param name="DeleteVolume">
/// Required, with no default. The brief's requirement is that deleting a database
/// must not delete its data unless the admin says so, and an optional boolean
/// defaulting to false is a weaker guarantee than a required one — an omitted
/// field is ambiguous, a required one is a decision.
/// </param>
public sealed record DeleteDatabaseRequest(string ConfirmSlug, bool DeleteVolume);

public sealed record DatabaseSummaryDto(
    Guid Id,
    string Slug,
    string DisplayName,
    string Engine,
    string Version,
    string State,
    DateTimeOffset StateChangedAt,
    long CpuNanos,
    long MemoryBytes,
    long StorageBytes,
    long? StorageUsedBytes,
    Guid? ActiveJobId,
    string DriftState,
    bool IsSystem)
{
    public static DatabaseSummaryDto From(DatabaseInstance d)
    {
        ArgumentNullException.ThrowIfNull(d);

        return new DatabaseSummaryDto(
            d.Id,
            d.Slug,
            d.DisplayName,
            d.Engine.ToString().ToLowerInvariant(),
            d.Version,
            Camel(d.State),
            new DateTimeOffset(d.StateChangedAt, TimeSpan.Zero),
            d.CpuLimitNanos,
            d.MemoryLimitBytes,
            d.StorageAllocationBytes,
            d.Volumes.Sum(v => v.LastMeasuredBytes) is { } measured and > 0 ? measured : null,
            d.ActiveJobId,
            d.DriftState.ToString().ToLowerInvariant(),
            IsSystem: false);
    }

    internal static string Camel(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}

public sealed record DatabaseBackupSettingsDto(
    bool Enabled,
    string? Cron,
    int? RetentionCount,
    int? RetentionDays);

public sealed record DatabaseDetailDto(
    DatabaseSummaryDto Summary,
    string ImageRef,
    string? ImageDigest,
    string ImageVariant,
    bool UsesCustomImage,
    string? DatabaseName,
    int? PublishedPort,
    string? PublishBindAddress,
    long? MaxMemoryBytes,
    string? MaxMemoryPolicy,
    bool? AofEnabled,
    DatabaseBackupSettingsDto Backup,
    IReadOnlyList<VolumeDto> Volumes,
    IReadOnlyList<WarningDto> Warnings)
{
    public static DatabaseDetailDto From(DatabaseInstance d, IReadOnlyList<WarningDto> warnings)
    {
        ArgumentNullException.ThrowIfNull(d);

        return new DatabaseDetailDto(
            DatabaseSummaryDto.From(d),
            d.ImageRef,
            d.ImageDigest,
            d.ImageVariant.ToString().ToLowerInvariant(),
            d.UsesCustomImage,
            // Null for Redis. Not an empty string — the field genuinely does not
            // apply, and the UI reads capabilities to know that.
            d.DatabaseName,
            d.PublishedPort,
            d.PublishBindAddress,
            d.MaxMemoryBytes,
            d.MaxMemoryPolicy,
            d.AofEnabled,
            new DatabaseBackupSettingsDto(
                d.BackupEnabled, d.BackupCron, d.BackupRetentionCount, d.BackupRetentionDays),
            [.. d.Volumes.Select(v => new VolumeDto(
                v.Id, v.Name, v.WorkloadId, d.Slug, v.Purpose.ToString().ToLowerInvariant(),
                v.SizeAllocationBytes, v.LastMeasuredBytes,
                v.MeasuredAt is null ? null : new DateTimeOffset(v.MeasuredAt.Value, TimeSpan.Zero),
                v.OrphanedAt is null ? null : new DateTimeOffset(v.OrphanedAt.Value, TimeSpan.Zero)))],
            warnings);
    }
}

public sealed record VolumeDto(
    Guid Id,
    string Name,
    Guid WorkloadId,
    string WorkloadSlug,
    string Purpose,
    long SizeAllocationBytes,
    long? LastMeasuredBytes,
    DateTimeOffset? MeasuredAt,
    DateTimeOffset? OrphanedAt);
