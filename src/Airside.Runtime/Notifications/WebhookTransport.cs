using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Airside.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Notifications;

/// <summary>
/// Posts a notification as JSON to a URL.
/// </summary>
/// <remarks>
/// Every request goes through <see cref="GuardedHttp"/>, so the destination is
/// checked at connect time and redirects are refused. See that type for why the
/// obvious alternative — validating the URL when it is saved — is not a check.
/// </remarks>
public sealed class WebhookTransport(HttpClient http, ILogger<WebhookTransport> logger) : INotificationTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ChannelKind Kind => ChannelKind.Webhook;

    public async Task<DeliveryOutcome> SendAsync(
        ChannelTarget channel,
        NotificationEnvelope notification,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(notification);

        var payload = JsonSerializer.Serialize(
            new
            {
                id = notification.NotificationId,
                level = notification.Level.ToString().ToLowerInvariant(),
                title = notification.Title,
                body = notification.Body,
                code = notification.Code,
                resource = notification.ResourceKind is null
                    ? null
                    : new { kind = notification.ResourceKind, id = notification.ResourceId },
                occurrences = notification.OccurrenceCount,
                firstSeenAt = notification.FirstSeenAt,
                lastSeenAt = notification.LastSeenAt,
                instance = notification.InstanceName,
                url = notification.DashboardUrl,
            },
            Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, channel.Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (channel.Secret is { } secret)
        {
            Sign(request, payload, secret.Reveal());
        }

        return await SendAsync(request, channel, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs the body so the receiver can tell a real notification from anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A webhook URL is usually the only thing standing between the receiver and
    /// anyone who has seen it in a log. The signature lets the receiver verify the
    /// body came from this Airside, and the timestamp — signed alongside the body,
    /// not beside it — is what stops a captured request being replayed later.
    /// </para>
    /// <para>
    /// The scheme deliberately matches the shape GitHub and Stripe use, because it
    /// is the one receivers already have code for.
    /// </para>
    /// </remarks>
    internal static void Sign(HttpRequestMessage request, string payload, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var signature = Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes($"{timestamp}.{payload}")))
            .ToLowerInvariant();

        request.Headers.TryAddWithoutValidation("X-Airside-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Airside-Signature", $"sha256={signature}");
    }

    private async Task<DeliveryOutcome> SendAsync(
        HttpRequestMessage request,
        ChannelTarget channel,
        CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return DeliveryOutcome.Delivered;
            }

            // A redirect is a failure here rather than something to follow: the
            // handler does not follow them, and a receiver answering 302 has
            // pointed us at a destination that has not been through the guard.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return DeliveryOutcome.Permanent(
                    $"The endpoint answered {(int)response.StatusCode} with a redirect, which Airside does "
                    + "not follow — a redirect target has not been checked against the outbound rules. "
                    + "Configure the final URL directly.");
            }

            // 4xx will be answered the same way next time, so retrying spends
            // attempts to reach an identical refusal. 429 is the exception: it is
            // the receiver asking for exactly that.
            var retryable = (int)response.StatusCode >= 500
                || response.StatusCode == HttpStatusCode.TooManyRequests;

            var detail = $"The endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}.";

            return retryable ? DeliveryOutcome.Transient(detail) : DeliveryOutcome.Permanent(detail);
        }
        catch (OutboundBlockedException ex)
        {
            // Configuration, not weather. Retrying will refuse it again.
            logger.LogWarning("Channel {Channel} was refused by the outbound guard: {Message}", channel.Name, ex.Message);

            return DeliveryOutcome.Permanent(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            // The guard throws from inside ConnectCallback, and HttpClient wraps
            // whatever comes out of there in an HttpRequestException — so the
            // catch above never sees it and a refused destination was being
            // classified as a temporary network problem. It then retried five
            // times and muted the channel, reporting a configuration mistake as an
            // outage.
            if (FindBlocked(ex) is { } blocked)
            {
                logger.LogWarning(
                    "Channel {Channel} was refused by the outbound guard: {Message}", channel.Name, blocked.Message);

                return DeliveryOutcome.Permanent(blocked.Message);
            }

            return DeliveryOutcome.Transient($"The endpoint could not be reached: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return DeliveryOutcome.Transient("The endpoint did not respond in time.");
        }
    }

    private static OutboundBlockedException? FindBlocked(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is OutboundBlockedException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    /// <summary>Sends a pre-built body, for the Slack transport's own payload shape.</summary>
    internal async Task<DeliveryOutcome> PostAsync(
        ChannelTarget channel,
        string url,
        string payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        return await SendAsync(request, channel, ct).ConfigureAwait(false);
    }
}
