using Airside.Core.Databases;

namespace Airside.Data.Entities;

public enum BackupTriggerKind
{
    Scheduled,
    Manual,

    /// <summary>Taken automatically before a restore, so "we restored the wrong backup" has an answer.</summary>
    PreRestore,

    PreUpdate,
}

public enum BackupStatus
{
    Running,
    Succeeded,
    Failed,
}

public class Backup : Entity
{
    public Guid DatabaseInstanceId { get; set; }

    public DatabaseInstance DatabaseInstance { get; set; } = null!;

    public Guid? JobId { get; set; }

    public BackupKind Kind { get; set; }

    public BackupTriggerKind TriggerKind { get; set; }

    public BackupStatus Status { get; set; } = BackupStatus.Running;

    public string StoragePath { get; set; } = string.Empty;

    public long? SizeBytes { get; set; }

    /// <summary>
    /// Verified before any restore begins. A truncated backup that restores as an
    /// empty database is the worst failure this feature has.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// The exact engine version this came from, e.g. <c>postgres:16.4</c>.
    /// </summary>
    /// <remarks>
    /// Not decoration. A pg_dump from 16 does not restore into 15, and a restore
    /// that discovers this halfway through has already stopped the database.
    /// </remarks>
    public string EngineSnapshot { get; set; } = string.Empty;

    public string? DatabaseNameSnapshot { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    /// <summary>Pins this backup against retention pruning.</summary>
    public bool IsRetained { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public string? ErrorMessage { get; set; }
}

public enum RestoreStatus
{
    Running,
    Succeeded,
    Failed,
}

public class Restore : Entity
{
    public Guid DatabaseInstanceId { get; set; }

    public DatabaseInstance DatabaseInstance { get; set; } = null!;

    public Guid BackupId { get; set; }

    public Backup Backup { get; set; } = null!;

    /// <summary>The safety backup taken immediately before this restore.</summary>
    public Guid? PreRestoreBackupId { get; set; }

    public Guid? JobId { get; set; }

    public RestoreStatus Status { get; set; } = RestoreStatus.Running;

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public Guid? RequestedByUserId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}

public class SavedQuery : Entity
{
    public Guid UserId { get; set; }

    public Guid? DatabaseInstanceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// One executed statement.
/// </summary>
/// <remarks>
/// A secret-bearing surface, and treated as one. A statement like
/// <c>INSERT INTO users (email, password) VALUES (…)</c> typed into the console
/// lands here as plain text, so history is strictly per-user, is never listable by
/// another user regardless of permission, is capped and pruned on write, and is
/// excluded from support bundles.
/// </remarks>
public class QueryHistoryEntry : Entity
{
    public Guid UserId { get; set; }

    public Guid DatabaseInstanceId { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTime ExecutedAt { get; set; }

    public int DurationMs { get; set; }

    public int RowsAffected { get; set; }

    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }
}
