using System.Text.Json;
using Airside.Core.Audit;
using Airside.Data.Entities;

namespace Airside.Data.Audit;

/// <summary>
/// Appends audit records. There is no update or delete member, by design.
/// </summary>
internal sealed class AuditWriter(AirsideDbContext db, TimeProvider timeProvider) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
            Action = entry.Action,
            Result = entry.Result,
            UserId = entry.UserId,
            UserEmailSnapshot = entry.UserEmailSnapshot,
            ResourceKind = entry.ResourceKind,
            ResourceId = entry.ResourceId,
            ResourceSlugSnapshot = entry.ResourceSlugSnapshot,
            IpAddress = entry.IpAddress,
            UserAgent = entry.UserAgent,
            CorrelationId = entry.CorrelationId,
            MetadataJson = entry.Metadata is null ? null : JsonSerializer.Serialize(entry.Metadata),
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
