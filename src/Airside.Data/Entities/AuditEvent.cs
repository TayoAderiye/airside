using Airside.Core.Audit;

namespace Airside.Data.Entities;

/// <summary>
/// An append-only record of a privileged action.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot fields are denormalised deliberately. An audit record must stay
/// readable after the user and the resource it refers to are gone, which is
/// precisely the case an audit log exists for. A row that renders as
/// "&lt;deleted user&gt; did something to &lt;deleted resource&gt;" is not a record.
/// </para>
/// <para>
/// Append-only is enforced three ways: no update or delete path in code, no such
/// endpoint, and a database-level guard in the provider-specific migration —
/// <c>REVOKE UPDATE, DELETE</c> on Postgres, a <c>BEFORE UPDATE … RAISE(FAIL)</c>
/// trigger on SQLite.
/// </para>
/// </remarks>
public class AuditEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTime OccurredAt { get; set; }

    public Guid? UserId { get; set; }

    public string? UserEmailSnapshot { get; set; }

    public string Action { get; set; } = string.Empty;

    public AuditResult Result { get; set; }

    public string? ResourceKind { get; set; }

    public Guid? ResourceId { get; set; }

    public string? ResourceSlugSnapshot { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>Never contains a secret value.</summary>
    public string? MetadataJson { get; set; }
}
