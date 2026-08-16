using Airside.Core.Operations;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Operations;

/// <inheritdoc />
public sealed class Notifier(
    AirsideDbContext db,
    TimeProvider timeProvider,
    ILogger<Notifier> logger) : INotifier
{
    public async Task RaiseAsync(NotificationRequest notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var existing = await db.Notifications
            .FirstOrDefaultAsync(n => n.DedupeKey == notification.DedupeKey && n.ResolvedAt == null, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // Refreshed in place. The body is overwritten because the same
            // condition changes degree — "expires in 7 days" should replace
            // "expires in 14 days" rather than sit beside it, which is what an
            // append-only list would produce four times a day.
            existing.LastSeenAt = now;
            existing.OccurrenceCount++;
            existing.Title = notification.Title;
            existing.Body = notification.Body;
            existing.Severity = Map(notification.Level);

            // A condition that has got worse is worth showing again to someone who
            // dismissed it while it was merely a warning.
            if (existing.Severity == NotificationSeverity.Error && existing.AcknowledgedAt is not null)
            {
                existing.AcknowledgedAt = null;
                existing.AcknowledgedByUserId = null;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        db.Notifications.Add(new Notification
        {
            Id = Guid.CreateVersion7(),
            DedupeKey = notification.DedupeKey,
            Severity = Map(notification.Level),
            Title = notification.Title,
            Body = notification.Body,
            Code = notification.Code,
            ResourceKind = notification.ResourceKind,
            ResourceId = notification.ResourceId,
            FirstSeenAt = now,
            LastSeenAt = now,
            OccurrenceCount = 1,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Notification raised: {Title} ({DedupeKey})", notification.Title, notification.DedupeKey);
    }

    public async Task ResolveAsync(string dedupeKey, CancellationToken ct)
    {
        var open = await db.Notifications
            .Where(n => n.DedupeKey == dedupeKey && n.ResolvedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (open.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Resolved, not deleted. Looking back and seeing that a certificate nearly
        // expired and was replaced is worth more than a clean list.
        foreach (var notification in open)
        {
            notification.ResolvedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static NotificationSeverity Map(NotificationLevel level) => level switch
    {
        NotificationLevel.Error => NotificationSeverity.Error,
        NotificationLevel.Warning => NotificationSeverity.Warning,
        _ => NotificationSeverity.Info,
    };
}

/// <summary>Dedupe keys, in one place so a raise and its matching resolve cannot drift apart.</summary>
public static class NotificationKeys
{
    public static string CertificateExpiring(string hostname) => $"certificate.expiring:{hostname}";

    public static string CertificateExpired(string hostname) => $"certificate.expired:{hostname}";

    public static string DomainFailed(string hostname) => $"domain.failed:{hostname}";

    public static string BackupFailed(Guid workloadId) => $"backup.failed:{workloadId}";

    public static string WorkloadUnhealthy(Guid workloadId) => $"workload.unhealthy:{workloadId}";

    public static string UpdateAvailable(string version) => $"update.available:{version}";

    public static string CertificateStoreMissing() => "certificate.store_missing";

    public static string ProxyDrift(string routeId) => $"proxy.drift:{routeId}";
}
