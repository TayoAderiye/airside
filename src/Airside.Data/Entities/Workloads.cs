using Airside.Core.Databases;
using Airside.Core.Workloads;

namespace Airside.Data.Entities;

/// <summary>
/// Anything Airside runs on behalf of a user.
/// </summary>
/// <remarks>
/// One table, table-per-hierarchy. Allocation, jobs, reconciliation, metrics, and
/// audit all operate on "a workload" uniformly; with separate tables every one of
/// those becomes a UNION, and the first one somebody forgets to update is a
/// resource-accounting bug that admits workloads the host cannot run. The cost is
/// a handful of nullable subtype columns, which is the better trade.
/// </remarks>
public abstract class Workload : Entity, ISoftDeletable
{
    public Guid HostId { get; set; }

    public Host Host { get; set; } = null!;

    public WorkloadKind Kind { get; set; }

    /// <summary>Validated as a <c>Slug</c> at the boundary. Every Docker name derives from this.</summary>
    public string Slug { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Stored as a string. The valid set and legal transitions belong to the
    /// subtype — see <c>WorkloadTransitions</c>.
    /// </summary>
    public string State { get; set; } = string.Empty;

    public DateTime StateChangedAt { get; set; }

    public long CpuLimitNanos { get; set; }

    public long MemoryLimitBytes { get; set; }

    /// <summary>
    /// Counted against host capacity for admission. Enforced by the kernel only
    /// where <c>Host.StorageEnforcement</c> is <c>Quota</c>; on every default EC2
    /// image it is accounting, and the API says so.
    /// </summary>
    public long StorageAllocationBytes { get; set; }

    public bool AutoRestart { get; set; } = true;

    public string? ContainerId { get; set; }

    public string? NetworkId { get; set; }

    public string? NetworkName { get; set; }

    /// <summary>Set while a job holds this workload. A second job queues behind it rather than failing.</summary>
    public Guid? ActiveJobId { get; set; }

    public DateTime? LastReconciledAt { get; set; }

    public DriftState DriftState { get; set; } = DriftState.None;

    public Guid? CreatedByUserId { get; set; }

    public DateTime? DeletedAt { get; set; }

    public ICollection<Volume> Volumes { get; } = new List<Volume>();
}

public enum DriftState
{
    None,
    Missing,
    Unexpected,
    Mismatched,
}

/// <summary>A managed database container.</summary>
public class DatabaseInstance : Workload
{
    public DatabaseEngineKind Engine { get; set; }

    public string Version { get; set; } = string.Empty;

    public string ImageRef { get; set; } = string.Empty;

    /// <summary>
    /// The base image this workload was created on.
    /// </summary>
    /// <remarks>
    /// Immutable after provisioning. Alpine and Debian builds differ in libc and
    /// in the on-disk layout an engine initialises, so switching one under an
    /// existing data volume is not a configuration change — it is a migration
    /// nobody asked for. Enforced at the service layer and again in
    /// <c>SaveChanges</c>.
    /// </remarks>
    public ImageVariant ImageVariant { get; set; }

    /// <summary>
    /// True when an explicit image was supplied instead of a resolved variant.
    /// </summary>
    /// <remarks>
    /// Airside cannot reason about what is inside a custom image, so variant
    /// guidance, version support, and upgrade advice all stop applying. Recording
    /// it means the UI can say so rather than showing guidance that is quietly
    /// untrue.
    /// </remarks>
    public bool UsesCustomImage { get; set; }

    /// <summary>
    /// Pinned at provision. A tag moves: <c>postgres:16</c> six months later is a
    /// different build, and a restart landing on it is how a database comes back
    /// refusing to start.
    /// </summary>
    public string? ImageDigest { get; set; }

    /// <summary>Null for Redis, which has 16 numbered logical databases and no name.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Null means not published to the host at all, which is the default.</summary>
    public int? PublishedPort { get; set; }

    /// <summary>Loopback unless the admin separately and explicitly opted into public exposure.</summary>
    public string? PublishBindAddress { get; set; }

    // Redis only. maxmemory is a separate resource axis from the container limit;
    // without it Redis grows until the cgroup OOM killer takes it, and the restart
    // looks like an unexplained crash rather than a configuration mistake.
    public long? MaxMemoryBytes { get; set; }

    public string? MaxMemoryPolicy { get; set; }

    public bool? AofEnabled { get; set; }

    public bool BackupEnabled { get; set; } = true;

    public string? BackupCron { get; set; }

    public int? BackupRetentionCount { get; set; }

    public int? BackupRetentionDays { get; set; }

    public ICollection<DatabaseCredential> Credentials { get; } = new List<DatabaseCredential>();

    public DatabaseState CurrentState =>
        Enum.TryParse<DatabaseState>(State, out var parsed) ? parsed : DatabaseState.Failed;
}

/// <summary>
/// A credential for a database.
/// </summary>
/// <remarks>
/// A table rather than two columns on the instance, so the history of who rotated
/// what and when survives, and so Redis 6+ ACL users arrive later as additional
/// rows rather than as a schema change.
/// <para>
/// It does <em>not</em> provide overlapping live credentials, and an earlier
/// version of this design wrongly assumed it did. Each engine stores one password
/// per role, so a rotation is a breaking change for everything currently
/// connected. Genuine overlap needs a second role — <c>CREATE ROLE app_2</c> with
/// the same grants — which is a larger feature, not a state on this row.
/// </para>
/// </remarks>
public class DatabaseCredential : Entity
{
    public Guid DatabaseInstanceId { get; set; }

    public DatabaseInstance DatabaseInstance { get; set; } = null!;

    /// <summary>Null for Redis: <c>requirepass</c> authenticates the implicit <c>default</c> user.</summary>
    public string? Username { get; set; }

    /// <summary>Data Protection ciphertext. The payload carries its own key identifier.</summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    public bool IsPrimary { get; set; }

    public CredentialState State { get; set; } = CredentialState.Active;

    public DateTime? RetiredAt { get; set; }

    public Guid? RotatedByUserId { get; set; }
}

public enum CredentialState
{
    Active,

    /// <summary>
    /// Replaced by a rotation and no longer accepted by the engine.
    /// </summary>
    /// <remarks>
    /// Not "superseded but still usable". Postgres, MySQL, and MongoDB each store
    /// one password per role, so <c>ALTER USER … PASSWORD</c> replaces it and the
    /// previous value stops authenticating immediately — confirmed against a live
    /// Postgres, which rejects the old password the moment rotation completes. The
    /// row survives for the audit trail, not because it still works.
    /// </remarks>
    Retired,

    Revoked,
}

/// <summary>
/// A named Docker volume.
/// </summary>
/// <remarks>
/// <see cref="WorkloadId"/> stays non-null and points at a soft-deleted workload
/// after an orphaning delete. That is what lets the reclaim screen say "12 GB,
/// formerly the orders Postgres, orphaned 14 March" instead of showing an
/// anonymous volume nobody dares remove.
/// </remarks>
public class Volume : Entity, ISoftDeletable
{
    public Guid HostId { get; set; }

    public Guid WorkloadId { get; set; }

    public Workload Workload { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public string MountPath { get; set; } = string.Empty;

    public VolumePurpose Purpose { get; set; } = VolumePurpose.Data;

    public long SizeAllocationBytes { get; set; }

    public long? LastMeasuredBytes { get; set; }

    public DateTime? MeasuredAt { get; set; }

    /// <summary>
    /// Set when the owning workload was deleted and the volume deliberately kept.
    /// Orphaned volumes keep counting against allocated storage — otherwise a few
    /// delete-and-recreate cycles quietly consume the disk with nothing in the UI
    /// explaining where it went.
    /// </summary>
    public DateTime? OrphanedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

public enum VolumePurpose
{
    Data,
    Backup,
    Config,
}
