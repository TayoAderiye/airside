using Airside.Core.Common;

namespace Airside.Core.Databases;

/// <summary>A provisioning request, after slug validation and before engine validation.</summary>
public sealed record DatabaseProvisionSpec
{
    public required Guid WorkloadId { get; init; }

    public required Slug Slug { get; init; }

    public required string DisplayName { get; init; }

    public required DatabaseEngineKind Engine { get; init; }

    public required string Version { get; init; }

    /// <summary>Null means the engine's own default — never a shared constant.</summary>
    public ImageVariant? Variant { get; init; }

    /// <summary>
    /// An explicit image, which bypasses variant resolution entirely.
    /// </summary>
    /// <remarks>
    /// The escape hatch for an image Airside does not publish a variant for — a
    /// Postgres build carrying pgvector or PostGIS, for instance. Airside cannot
    /// reason about what is inside it, so the workload is flagged
    /// <c>UsesCustomImage</c> and variant guidance stops applying.
    /// </remarks>
    public string? CustomImage { get; init; }

    public required long CpuNanos { get; init; }

    public required long MemoryBytes { get; init; }

    public required long StorageBytes { get; init; }

    public bool AutoRestart { get; init; } = true;

    /// <summary>Null means not published to the host at all, which is the default.</summary>
    public int? PublishedPort { get; init; }

    /// <summary>Loopback unless the admin explicitly and separately opted into public exposure.</summary>
    public string PublishBindAddress { get; init; } = Containers.PortBinding.Loopback;

    /// <summary>Null for Redis, required for the others.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>Null for Redis, required for the others.</summary>
    public string? Username { get; init; }

    public required Secret Password { get; init; }

    // Redis only.
    public long? MaxMemoryBytes { get; init; }

    public string? MaxMemoryPolicy { get; init; }

    public bool? AofEnabled { get; init; }

    public bool BackupEnabled { get; init; } = true;
}

/// <summary>Names resolved by the platform, handed to the engine so it never invents its own.</summary>
public sealed record ProvisionContext(
    string ContainerName,
    string NetworkName,
    string DataVolumeName,
    IReadOnlyDictionary<string, string> Labels);

public sealed record DatabaseEndpoint(
    string ContainerId,
    string HostName,
    int Port,
    string? DatabaseName);

public sealed record DatabaseCredentialValue(string? Username, Secret Password);

/// <summary>
/// A recommended <c>maxmemory</c>, with the reason it was chosen.
/// </summary>
/// <remarks>
/// The default is 70% of the container limit when persistence is on, not 80%.
/// <c>maxmemory</c> bounds the dataset only; during BGSAVE or AOF rewrite Redis
/// forks, and copy-on-write grows the fork's resident set toward the parent's as
/// writes land. At 80% with backups enabled a write-heavy instance gets
/// OOM-killed mid-backup and restarts, which reads as a mystery crash rather than
/// a backup problem. 80% is right only for a pure cache with RDB and AOF both off.
/// </remarks>
public sealed record MaxMemoryRecommendation(long Bytes, double FractionOfLimit, string Reason);

public sealed record DatabaseProbeResult(bool IsReachable, bool IsAcceptingWrites, string? Detail);

/// <param name="EngineSnapshot">
/// The exact engine version, e.g. <c>postgres:16</c>. Supplied by the caller and
/// recorded on the backup, because a restore has to refuse a major-version
/// mismatch before it stops anything.
/// </param>
public sealed record BackupOperation(
    DatabaseEndpoint Endpoint,
    DatabaseCredentialValue Credential,
    string DataVolumeName,
    string EngineSnapshot,
    IProgress<string>? Progress);

public sealed record RestoreOperation(
    DatabaseEndpoint Endpoint,
    DatabaseCredentialValue Credential,
    string DataVolumeName,
    string BackupEngineSnapshot,
    IProgress<string>? Progress);

/// <param name="EngineSnapshot">
/// The exact engine version the backup came from, e.g. <c>postgres:16.4</c>. A
/// pg_dump from 16 does not restore into 15, and a restore that discovers this
/// halfway through has already stopped the database.
/// </param>
public sealed record BackupArtifact(
    long SizeBytes,
    string Sha256,
    string EngineSnapshot,
    BackupKind Kind);

public enum BackupKind
{
    /// <summary>pg_dump, mysqldump, mongodump.</summary>
    Logical,

    /// <summary>A Redis RDB file copied out of the volume after BGSAVE.</summary>
    Snapshot,
}

/// <summary>Connection details for an attached database, plus a ready-built connection string.</summary>
public sealed record ConnectionDetails(
    string Host,
    int Port,
    string? DatabaseName,
    string? Username,
    Secret Password,
    Secret ConnectionString);
