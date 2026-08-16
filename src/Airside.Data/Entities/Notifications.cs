using Airside.Core.Notifications;

namespace Airside.Data.Entities;

/// <summary>
/// Somewhere notifications are sent.
/// </summary>
/// <remarks>
/// The secret — a webhook signing key, a Slack URL's token, an SMTP password — is
/// encrypted with the Data Protection key ring, on the same path as database
/// credentials: masked in every response, never logged, revealed only through an
/// audited endpoint.
/// </remarks>
public class NotificationChannel : Entity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;

    public ChannelKind Kind { get; set; }

    /// <summary>
    /// A URL for webhook and Slack, or a recipient address for email.
    /// </summary>
    /// <remarks>
    /// For Slack this is the incoming-webhook URL, which <em>is</em> the credential
    /// — anyone holding it can post to the channel. It is stored encrypted rather
    /// than here, and this field holds only the host for display.
    /// </remarks>
    public string Endpoint { get; set; } = string.Empty;

    public string? EncryptedSecret { get; set; }

    /// <summary>Transport-specific settings as JSON. Never secrets.</summary>
    public string SettingsJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The lowest severity that reaches this channel.
    /// </summary>
    /// <remarks>
    /// Per channel because the useful arrangement is a chat channel that gets
    /// everything and an email address that gets only what would wake someone up.
    /// One global threshold forces the choice between noise and missing things.
    /// </remarks>
    public NotificationSeverity MinimumSeverity { get; set; } = NotificationSeverity.Warning;

    /// <summary>
    /// Routing rules beyond severity, as JSON. Empty means everything.
    /// </summary>
    /// <remarks>
    /// Empty is "send everything" rather than "send nothing", which is what makes
    /// this safe to add to channels that already exist — the alternative would
    /// have silently stopped every one of them the moment the column appeared.
    /// </remarks>
    public string RoutingJson { get; set; } = "{}";

    public DateTime? LastAttemptAt { get; set; }

    public bool? LastAttemptSucceeded { get; set; }

    public string? LastAttemptError { get; set; }

    /// <summary>
    /// Consecutive failures, used to back a broken channel off.
    /// </summary>
    /// <remarks>
    /// A channel whose receiver has been down for a day should not be retried at
    /// full rate for every notification raised since — that turns one outage into
    /// a queue that takes hours to drain once it recovers.
    /// </remarks>
    public int ConsecutiveFailures { get; set; }

    public DateTime? MutedUntil { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTime? DeletedAt { get; set; }
}

public enum DeliveryStatus
{
    Pending,
    Delivered,
    Failed,

    /// <summary>Not attempted — below the channel's threshold, or the channel was disabled.</summary>
    Skipped,
}

/// <summary>
/// One notification's journey to one channel.
/// </summary>
/// <remarks>
/// A row per notification per channel, so a webhook that failed can be retried
/// without re-sending to the email address that already succeeded. Without it,
/// retry means re-delivering everything, and the safest implementation of that is
/// to never retry at all.
/// </remarks>
public class NotificationDelivery : Entity
{
    public Guid NotificationId { get; set; }

    public Guid ChannelId { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    public int Attempts { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// <summary>When the next attempt becomes due. Null once the row is settled.</summary>
    public DateTime? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// Why a notification was not offered to this channel.
    /// </summary>
    /// <remarks>
    /// The answer to "why did Slack not get this". Without it a filtered
    /// notification and a silently broken channel look identical from the outside,
    /// which is the failure mode routing rules introduce.
    /// </remarks>
    public string? SkipReason { get; set; }

    public DateTime? DeliveredAt { get; set; }
}
