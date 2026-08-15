namespace Airside.Core.Audit;

public enum AuditResult
{
    Success,
    Failure,

    /// <summary>An authorisation refusal. A denied secret.reveal is more interesting than a successful one.</summary>
    Denied,
}

/// <summary>
/// One audit record.
/// </summary>
/// <remarks>
/// The snapshot fields are denormalised deliberately. An audit record must stay
/// readable after the user and the resource it refers to are gone, which is
/// exactly the case an audit log exists for.
/// </remarks>
public sealed record AuditEntry
{
    public required string Action { get; init; }

    public required AuditResult Result { get; init; }

    public Guid? UserId { get; init; }

    public string? UserEmailSnapshot { get; init; }

    public string? ResourceKind { get; init; }

    public Guid? ResourceId { get; init; }

    public string? ResourceSlugSnapshot { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Never contains a secret value. Enforced by the Serilog destructuring policy and by review.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Appends to the audit log.
/// </summary>
/// <remarks>
/// There is no update, no delete, and no interface member offering either.
/// Append-only is enforced three ways: no path in code, no endpoint, and a
/// database-level guard in the provider-specific migration — <c>REVOKE UPDATE,
/// DELETE</c> on Postgres, a <c>BEFORE UPDATE … RAISE(FAIL)</c> trigger on SQLite.
/// </remarks>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

/// <summary>Actions that must produce an audit record.</summary>
public static class AuditActions
{
    public const string UserSignIn = "user.sign_in";
    public const string UserSignInFailed = "user.sign_in_failed";
    public const string UserCreated = "user.created";
    public const string UserDeactivated = "user.deactivated";
    public const string PermissionsChanged = "user.permissions_changed";

    public const string DatabaseProvisioned = "database.provisioned";
    public const string DatabaseDeleted = "database.deleted";
    public const string DatabaseResized = "database.resized";
    public const string DatabaseBackedUp = "database.backed_up";
    public const string DatabaseRestored = "database.restored";
    public const string CredentialsRotated = "database.credentials_rotated";
    public const string QueryExecuted = "database.query_executed";

    public const string ApplicationDeployed = "application.deployed";
    public const string ApplicationRolledBack = "application.rolled_back";
    public const string ApplicationDeleted = "application.deleted";
    public const string DatabaseAttached = "application.database_attached";
    public const string DatabaseDetached = "application.database_detached";

    public const string SecretRevealed = "secret.revealed";
    public const string SecretChanged = "secret.changed";

    public const string VolumeDeleted = "volume.deleted";
    public const string SystemUpdated = "system.updated";
    public const string SystemRolledBack = "system.rolled_back";
}
