using System.Text.Json;
using Airside.Core.Notifications;
using Airside.Core.Operations;

namespace Airside.Runtime.Notifications;

/// <summary>
/// Posts to a Slack incoming webhook.
/// </summary>
/// <remarks>
/// <para>
/// A thin shape over <see cref="WebhookTransport"/>, because that is what a Slack
/// incoming webhook is — a URL that accepts a JSON POST. What differs is the
/// payload, and that difference matters: Slack renders a bare <c>text</c> field as
/// one grey line, so an alert about a certificate expiring in three days looks
/// identical to a routine notice.
/// </para>
/// <para>
/// The <b>URL is the credential</b>. Anyone holding it can post to the channel,
/// so it is stored encrypted rather than in the endpoint column, and the endpoint
/// column keeps only the host for display.
/// </para>
/// </remarks>
public sealed class SlackTransport(WebhookTransport webhooks) : INotificationTransport
{
    public ChannelKind Kind => ChannelKind.Slack;

    public Task<DeliveryOutcome> SendAsync(
        ChannelTarget channel,
        NotificationEnvelope notification,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(notification);

        // The whole URL is the secret. Falling back to Endpoint would post to a
        // host with no token path and get a 404 that reads as "Slack is down".
        var url = channel.Secret?.Reveal() ?? channel.Endpoint;

        var blocks = new List<object>
        {
            new
            {
                type = "header",
                text = new { type = "plain_text", text = $"{Emoji(notification.Level)} {notification.Title}" },
            },
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text = notification.Body },
            },
        };

        var context = new List<string> { $"*{notification.InstanceName}*" };

        if (notification.OccurrenceCount > 1)
        {
            // Because dedupe means one message stands for many observations, and
            // "seen 14 times" is the difference between a blip and a pattern.
            context.Add($"seen {notification.OccurrenceCount} times since {notification.FirstSeenAt:u}");
        }

        if (notification.Code is not null)
        {
            context.Add($"`{notification.Code}`");
        }

        blocks.Add(new
        {
            type = "context",
            elements = new[] { new { type = "mrkdwn", text = string.Join("  •  ", context) } },
        });

        if (notification.DashboardUrl is not null)
        {
            blocks.Add(new
            {
                type = "actions",
                elements = new[]
                {
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "Open Airside" },
                        url = notification.DashboardUrl,
                    },
                },
            });
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                // Kept alongside the blocks: it is what Slack shows in the
                // notification bar and in clients that do not render blocks, and
                // omitting it produces a push alert that says "New message".
                text = $"{notification.Title} — {notification.InstanceName}",
                blocks,
            },
            SlackJson);

        return webhooks.PostAsync(channel, url, payload, ct);
    }

    private static readonly JsonSerializerOptions SlackJson = new(JsonSerializerDefaults.Web);

    private static string Emoji(NotificationLevel level) => level switch
    {
        NotificationLevel.Error => "\U0001F534",
        NotificationLevel.Warning => "\U0001F7E0",
        _ => "\U0001F535",
    };
}
