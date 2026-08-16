namespace Airside.Data.Entities;

/// <summary>
/// One hour of resource usage for one workload.
/// </summary>
/// <remarks>
/// <para>
/// Rollups only — raw samples are never persisted. A sample every fifteen seconds
/// for twenty workloads is five million rows a month, on a control plane whose
/// database is expected to sit on the same disk as the workloads it manages. The
/// question anyone actually asks of this data is "was it busy on Tuesday", which
/// an hourly min/avg/max answers.
/// </para>
/// <para>
/// The cost is that a spike shorter than an hour shows up only as the max. That is
/// a real loss, and the right trade for a single-server tool: anyone who needs
/// per-second history wants Prometheus, and should point it at the same daemon.
/// </para>
/// </remarks>
public class MetricRollup : Entity
{
    public Guid WorkloadId { get; set; }

    /// <summary>The hour this covers, truncated to the hour in UTC.</summary>
    public DateTime HourUtc { get; set; }

    public int SampleCount { get; set; }

    /// <summary>
    /// Nanoseconds of CPU consumed per elapsed second.
    /// </summary>
    /// <remarks>
    /// Not a percentage. This is the same unit as the workload's CPU limit, so a
    /// chart can show usage against the allocation rather than against the host —
    /// which is the comparison that tells an operator whether to resize.
    /// </remarks>
    public long CpuNanosAvg { get; set; }

    public long CpuNanosMax { get; set; }

    public long CpuLimitNanos { get; set; }

    public long MemoryBytesAvg { get; set; }

    public long MemoryBytesMax { get; set; }

    public long MemoryLimitBytes { get; set; }

    public long NetworkRxBytes { get; set; }

    public long NetworkTxBytes { get; set; }
}

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Something an operator should know about, whether or not they are looking.
/// </summary>
/// <remarks>
/// <para>
/// Deduplicated by <see cref="DedupeKey"/> rather than appended blindly. A
/// certificate expiring in seven days is one fact, and the expiry sweep notices it
/// every six hours — without dedupe that is four identical rows a day, twenty-eight
/// before the certificate expires, and a notification list nobody reads.
/// </para>
/// <para>
/// The same key is reused when the underlying condition changes degree, so
/// "expires in 7 days" replaces "expires in 14 days" instead of accumulating
/// beside it.
/// </para>
/// </remarks>
public class Notification : Entity
{
    /// <summary>
    /// Identifies the underlying condition, not the occurrence.
    /// </summary>
    /// <remarks>
    /// Something like <c>certificate.expiring:app.example.com</c>. Two
    /// notifications with the same key are the same fact observed twice.
    /// </remarks>
    public string DedupeKey { get; set; } = string.Empty;

    public NotificationSeverity Severity { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>A stable code the UI can key a help link or an action off.</summary>
    public string? Code { get; set; }

    public string? ResourceKind { get; set; }

    public Guid? ResourceId { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    /// <summary>How many times the condition has been observed since it was first raised.</summary>
    public int OccurrenceCount { get; set; } = 1;

    public DateTime? AcknowledgedAt { get; set; }

    public Guid? AcknowledgedByUserId { get; set; }

    /// <summary>
    /// Set when the condition stopped being true.
    /// </summary>
    /// <remarks>
    /// Resolved rather than deleted, so "this was broken and fixed itself" is
    /// distinguishable from "this never happened" when someone looks back.
    /// </remarks>
    public DateTime? ResolvedAt { get; set; }
}

public enum UpdateStatus
{
    Pending,
    Downloading,
    Migrating,
    Starting,
    HealthChecking,
    Succeeded,
    RollingBack,
    RolledBack,
    Failed,
}

/// <summary>
/// One attempt to update the control plane.
/// </summary>
/// <remarks>
/// <para>
/// A durable record because the process performing the update is the one being
/// replaced. Whatever is running afterwards has to be able to work out what
/// happened from the database and <c>state.json</c> alone — there is no surviving
/// caller to report to.
/// </para>
/// <para>
/// This is also the record the CLI reads to finish an update whose updater died
/// mid-way, which is the failure this whole design exists to survive.
/// </para>
/// </remarks>
public class UpdateRecord : Entity
{
    public string FromVersion { get; set; } = string.Empty;

    public string ToVersion { get; set; } = string.Empty;

    /// <summary>The exact image the previous version ran, for rollback by digest rather than by tag.</summary>
    public string? FromImageDigest { get; set; }

    public string? ToImageDigest { get; set; }

    public UpdateStatus Status { get; set; } = UpdateStatus.Pending;

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Whether the database schema changed, which decides whether rollback is safe.</summary>
    public bool AppliedMigrations { get; set; }

    /// <summary>A system backup taken before anything was changed.</summary>
    public string? PreUpdateBackupPath { get; set; }

    public Guid? StartedByUserId { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// A second factor for a user.
/// </summary>
/// <remarks>
/// The shared secret is encrypted with the Data Protection key ring, like every
/// other secret. Recovery codes are stored hashed and one-way: a code that can be
/// read out of the database is a second factor that a database dump defeats.
/// </remarks>
public class UserMfa : Entity
{
    public Guid UserId { get; set; }

    public string EncryptedSecret { get; set; } = string.Empty;

    /// <summary>Newline-separated hashes. Never the codes themselves.</summary>
    public string RecoveryCodeHashes { get; set; } = string.Empty;

    /// <summary>
    /// Null until the first correct code is entered.
    /// </summary>
    /// <remarks>
    /// Enrolment is not complete when the secret is generated, only when the user
    /// proves their authenticator holds it. Marking it confirmed at generation
    /// would lock people out of their own account with a QR code they never
    /// scanned successfully.
    /// </remarks>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>
    /// The last accepted time step, so a code cannot be used twice.
    /// </summary>
    /// <remarks>
    /// TOTP codes stay valid for their whole window, so without this a code
    /// captured in transit can be replayed for the next thirty seconds.
    /// </remarks>
    public long LastUsedTimeStep { get; set; }
}
