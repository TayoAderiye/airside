using System.Text.Json;
using Airside.Core.Notifications;
using Airside.Core.Operations;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Notifications;

public sealed class DispatchOptions
{
    public const string Section = "Airside:Notifications";

    /// <summary>How many times a transient failure is retried before the delivery is abandoned.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Consecutive failures before a channel is muted.
    /// </summary>
    /// <remarks>
    /// A receiver that has been down for a day should not be retried at full rate
    /// for every notification raised since — that turns one outage into a queue
    /// that takes hours to drain once it recovers, delivering alerts about things
    /// long since fixed.
    /// </remarks>
    public int FailuresBeforeMuting { get; set; } = 10;

    public int MuteMinutes { get; set; } = 30;

    /// <summary>
    /// Permits webhooks to private addresses.
    /// </summary>
    /// <remarks>
    /// Off, and it should stay off unless somebody genuinely runs a receiver on
    /// the local network. Turning it on also makes Caddy's unauthenticated admin
    /// API reachable from a notification channel — see <c>OutboundGuard</c>.
    /// </remarks>
    public bool AllowPrivateDestinations { get; set; }
}

/// <summary>
/// Delivers raised notifications to the configured channels.
/// </summary>
/// <remarks>
/// <para>
/// Separate from raising them, and deliberately so. <c>Notifier</c> is called from
/// sweeps and job handlers, and a webhook receiver that takes thirty seconds to
/// answer must not hold up a certificate check — nor should a delivery failure
/// turn into a failed job.
/// </para>
/// <para>
/// Delivery is tracked per notification per channel, so a webhook that failed is
/// retried without re-sending to the email address that already succeeded. Without
/// that, "retry" means "send everything again", and the safest implementation of
/// that is to never retry at all.
/// </para>
/// </remarks>
public sealed class NotificationDispatcher(
    IServiceScopeFactory scopeFactory,
    IEnumerable<INotificationTransport> transports,
    DispatchOptions options,
    TimeProvider timeProvider,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);

        do
        {
            await DispatchAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

            var channels = await db.NotificationChannels
                .Where(c => c.Enabled)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (channels.Count == 0)
            {
                return;
            }

            await EnqueueAsync(db, channels, ct).ConfigureAwait(false);
            await SendDueAsync(db, channels, scope, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Dispatch must never take the process down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Notification dispatch failed; the next tick will try again");
        }
    }

    /// <summary>
    /// Creates a delivery row for every notification that has not been offered to a channel yet.
    /// </summary>
    /// <remarks>
    /// Rows are created even when the notification is below the channel's
    /// threshold, marked <see cref="DeliveryStatus.Skipped"/>. That costs a row and
    /// buys the answer to "why did this not reach Slack", which is otherwise
    /// indistinguishable from a delivery that failed silently.
    /// </remarks>
    private async Task EnqueueAsync(
        AirsideDbContext db,
        List<NotificationChannel> channels,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Only unresolved notifications, and only recent ones. A channel added
        // today should not replay a fortnight of history at whoever configured it.
        var horizon = now.AddHours(-1);

        var pending = await db.Notifications
            .Where(n => n.ResolvedAt == null && n.LastSeenAt >= horizon)
            .OrderBy(n => n.FirstSeenAt)
            .Take(200)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return;
        }

        var notificationIds = pending.ConvertAll(n => n.Id);

        var existing = await db.NotificationDeliveries
            .Where(d => notificationIds.Contains(d.NotificationId))
            .Select(d => new { d.NotificationId, d.ChannelId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var known = existing
            .Select(e => (e.NotificationId, e.ChannelId))
            .ToHashSet();

        foreach (var notification in pending)
        {
            foreach (var channel in channels)
            {
                if (known.Contains((notification.Id, channel.Id)))
                {
                    continue;
                }

                // Severity and the routing rules are decided together, by the same
                // function the preview endpoint calls — so what an operator is
                // shown before saving a rule is what actually happens.
                var decision = NotificationRouter.Evaluate(
                    NotificationRoute.FromJson(channel.RoutingJson),
                    ToLevel(notification.Severity),
                    ToLevel(channel.MinimumSeverity),
                    notification.Code,
                    notification.ResourceKind,
                    notification.ResourceId);

                if (!decision.Matches)
                {
                    db.NotificationDeliveries.Add(new NotificationDelivery
                    {
                        Id = Guid.CreateVersion7(),
                        NotificationId = notification.Id,
                        ChannelId = channel.Id,
                        Status = DeliveryStatus.Skipped,
                        SkipReason = decision.Reason,
                    });

                    continue;
                }

                // The schedule is asked second, because "this channel is not about
                // that" is a better answer than "this channel is asleep" when both
                // are true.
                var window = NotificationScheduler.Evaluate(
                    NotificationSchedule.FromJson(channel.ScheduleJson),
                    ToLevel(notification.Severity),
                    timeProvider.GetUtcNow());

                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    Id = Guid.CreateVersion7(),
                    NotificationId = notification.Id,
                    ChannelId = channel.Id,

                    // A deferred delivery stays Pending with a later due time, so
                    // it rides the same retry machinery rather than needing a
                    // second one.
                    Status = window.IsOpen || window.OpensAt is not null
                        ? DeliveryStatus.Pending
                        : DeliveryStatus.Skipped,
                    NextAttemptAt = window.IsOpen ? now : window.OpensAt?.UtcDateTime,
                    SkipReason = window.IsOpen ? null : window.Reason,
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SendDueAsync(
        AirsideDbContext db,
        List<NotificationChannel> channels,
        IServiceScope scope,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var due = await db.NotificationDeliveries
            .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt != null && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (due.Count == 0)
        {
            return;
        }

        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var settings = await db.InstanceSettings.AsNoTracking().FirstAsync(ct).ConfigureAwait(false);

        foreach (var delivery in due)
        {
            var channel = channels.Find(c => c.Id == delivery.ChannelId);

            if (channel is null || (channel.MutedUntil is { } muted && muted > now))
            {
                continue;
            }

            var notification = await db.Notifications
                .AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == delivery.NotificationId, ct)
                .ConfigureAwait(false);

            if (notification is null)
            {
                delivery.Status = DeliveryStatus.Skipped;
                delivery.NextAttemptAt = null;
                continue;
            }

            // A notification held overnight and fixed before morning should not
            // arrive at nine o'clock announcing a problem that no longer exists.
            // Only deferred deliveries are dropped this way: one that was already
            // due is a genuine delivery attempt, and resolving mid-flight should
            // not swallow it.
            if (notification.ResolvedAt is not null && delivery.Attempts == 0 && delivery.LastAttemptAt is null)
            {
                delivery.Status = DeliveryStatus.Skipped;
                delivery.NextAttemptAt = null;
                delivery.SkipReason = "resolved before this channel's hours began";

                continue;
            }

            // Re-checked rather than trusted from enqueue time, so an edited
            // schedule takes effect on work already queued.
            var window = NotificationScheduler.Evaluate(
                NotificationSchedule.FromJson(channel.ScheduleJson),
                ToLevel(notification.Severity),
                timeProvider.GetUtcNow());

            if (!window.IsOpen)
            {
                delivery.NextAttemptAt = window.OpensAt?.UtcDateTime;
                delivery.SkipReason = window.Reason;

                if (window.OpensAt is null)
                {
                    delivery.Status = DeliveryStatus.Skipped;
                }

                continue;
            }

            delivery.SkipReason = null;

            await AttemptAsync(db, delivery, channel, notification, settings, protector, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task AttemptAsync(
        AirsideDbContext db,
        NotificationDelivery delivery,
        NotificationChannel channel,
        Notification notification,
        InstanceSettings settings,
        ISecretProtector protector,
        CancellationToken ct)
    {
        var transport = transports.FirstOrDefault(t => t.Kind == channel.Kind);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        delivery.Attempts++;
        delivery.LastAttemptAt = now;

        if (transport is null)
        {
            delivery.Status = DeliveryStatus.Failed;
            delivery.NextAttemptAt = null;
            delivery.LastError = $"No transport is registered for {channel.Kind}.";

            return;
        }

        var target = BuildTarget(channel, protector);
        var envelope = BuildEnvelope(notification, settings);

        DeliveryOutcome outcome;

        try
        {
            outcome = await transport.SendAsync(target, envelope, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A misbehaving transport must not stop the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome = DeliveryOutcome.Transient(ex.Message);
        }

        channel.LastAttemptAt = now;
        channel.LastAttemptSucceeded = outcome.Succeeded;
        channel.LastAttemptError = outcome.Succeeded
            ? null
            : Truncate(outcome.Detail);

        if (outcome.Succeeded)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.DeliveredAt = now;
            delivery.NextAttemptAt = null;
            delivery.LastError = null;
            channel.ConsecutiveFailures = 0;
            channel.MutedUntil = null;

            return;
        }

        delivery.LastError = Truncate(outcome.Detail);
        channel.ConsecutiveFailures++;

        // A permanent failure is a configuration answer, not weather. Retrying a
        // revoked token or a refused destination spends attempts to be told the
        // same thing, and against a signed webhook it looks like an attack.
        if (!outcome.Retryable || delivery.Attempts >= options.MaxAttempts)
        {
            delivery.Status = DeliveryStatus.Failed;
            delivery.NextAttemptAt = null;

            logger.LogWarning(
                "Giving up delivering notification {Notification} to {Channel} after {Attempts} attempt(s): {Error}",
                notification.Id, channel.Name, delivery.Attempts, delivery.LastError);
        }
        else
        {
            // Exponential, so a receiver that is down is not hammered while it
            // recovers: roughly 30s, 1m, 2m, 4m.
            var backoff = TimeSpan.FromSeconds(30 * Math.Pow(2, delivery.Attempts - 1));
            delivery.NextAttemptAt = now.Add(backoff);
        }

        if (channel.ConsecutiveFailures >= options.FailuresBeforeMuting && channel.MutedUntil is null)
        {
            channel.MutedUntil = now.AddMinutes(options.MuteMinutes);

            logger.LogWarning(
                "Muting channel {Channel} for {Minutes} minutes after {Failures} consecutive failures. "
                + "Notifications are still recorded and visible in Airside.",
                channel.Name, options.MuteMinutes, channel.ConsecutiveFailures);
        }
    }

    /// <summary>Decrypts the channel's secret for the duration of one send.</summary>
    private ChannelTarget BuildTarget(NotificationChannel channel, ISecretProtector protector)
    {
        Core.Common.Secret? secret = null;

        if (!string.IsNullOrEmpty(channel.EncryptedSecret))
        {
            var unprotected = protector.Unprotect(channel.EncryptedSecret);

            if (unprotected.IsSuccess)
            {
                secret = unprotected.Value;
            }
            else
            {
                logger.LogError(
                    "The secret for channel {Channel} could not be decrypted. This usually means the Data "
                    + "Protection key ring was replaced. Re-enter the channel's credentials.",
                    channel.Name);
            }
        }

        var settings = string.IsNullOrWhiteSpace(channel.SettingsJson)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(channel.SettingsJson)
                ?? [];

        return new ChannelTarget(channel.Id, channel.Name, channel.Kind, channel.Endpoint, secret, settings);
    }

    private static NotificationSeverityLevel ToLevel(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => NotificationSeverityLevel.Error,
        NotificationSeverity.Warning => NotificationSeverityLevel.Warning,
        _ => NotificationSeverityLevel.Info,
    };

    private static NotificationEnvelope BuildEnvelope(Notification notification, InstanceSettings settings) =>
        new(
            notification.Id,
            notification.Severity switch
            {
                NotificationSeverity.Error => NotificationLevel.Error,
                NotificationSeverity.Warning => NotificationLevel.Warning,
                _ => NotificationLevel.Info,
            },
            notification.Title,
            notification.Body,
            notification.Code,
            notification.ResourceKind,
            notification.ResourceId,
            notification.OccurrenceCount,
            new DateTimeOffset(notification.FirstSeenAt, TimeSpan.Zero),
            new DateTimeOffset(notification.LastSeenAt, TimeSpan.Zero),
            settings.InstanceName,

            // Only when a dashboard domain is set. A link to an IP address that
            // the recipient cannot reach is worse than no link.
            settings.DashboardDomain is null ? null : $"https://{settings.DashboardDomain}/notifications");

    private static string? Truncate(string? detail) =>
        detail is null ? null : detail.Length <= 500 ? detail : detail[..500];
}
