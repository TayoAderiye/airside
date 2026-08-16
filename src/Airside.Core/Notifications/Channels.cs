using Airside.Core.Common;
using Airside.Core.Operations;

namespace Airside.Core.Notifications;

public enum ChannelKind
{
    Webhook,
    Slack,
    Email,
}

/// <summary>
/// One delivery attempt's worth of channel configuration, with secrets in hand.
/// </summary>
/// <remarks>
/// Assembled per send rather than held, so the decrypted secret exists for the
/// duration of one request and is not a field on a long-lived object that might
/// end up in a heap dump or a log line.
/// </remarks>
public sealed record ChannelTarget(
    Guid ChannelId,
    string Name,
    ChannelKind Kind,
    string Endpoint,
    Secret? Secret,
    IReadOnlyDictionary<string, string> Settings);

/// <param name="InstanceName">
/// Included in every message. Somebody running Airside on three servers gets
/// three sets of alerts, and "a certificate is expiring" is useless without
/// knowing which host said so.
/// </param>
public sealed record NotificationEnvelope(
    Guid NotificationId,
    NotificationLevel Level,
    string Title,
    string Body,
    string? Code,
    string? ResourceKind,
    Guid? ResourceId,
    int OccurrenceCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string InstanceName,
    string? DashboardUrl);

/// <summary>Sends a notification somewhere outside Airside.</summary>
public interface INotificationTransport
{
    ChannelKind Kind { get; }

    Task<DeliveryOutcome> SendAsync(ChannelTarget channel, NotificationEnvelope notification, CancellationToken ct);
}

/// <param name="Retryable">
/// False for a rejection the receiver will make again — a bad URL, a revoked
/// token, a refused destination. Retrying those spends attempts to reach the same
/// answer, and on a webhook with a signing secret it also looks like an attack.
/// </param>
public sealed record DeliveryOutcome(bool Succeeded, bool Retryable, string? Detail = null)
{
    public static DeliveryOutcome Delivered { get; } = new(true, false);

    public static DeliveryOutcome Permanent(string detail) => new(false, false, detail);

    public static DeliveryOutcome Transient(string detail) => new(false, true, detail);
}
