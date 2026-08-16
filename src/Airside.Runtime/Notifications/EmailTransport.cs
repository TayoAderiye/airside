using System.Globalization;
using Airside.Core.Notifications;
using Airside.Core.Operations;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Airside.Runtime.Notifications;

/// <summary>
/// Sends a notification by email over SMTP.
/// </summary>
/// <remarks>
/// A fresh connection per message rather than a pooled one. Notifications are
/// infrequent, and a long-lived SMTP connection is the kind that turns out to have
/// been dropped by the provider hours ago — discovered at the moment something
/// urgent needed sending.
/// </remarks>
public sealed class EmailTransport(ILogger<EmailTransport> logger) : INotificationTransport
{
    public ChannelKind Kind => ChannelKind.Email;

    public async Task<DeliveryOutcome> SendAsync(
        ChannelTarget channel,
        NotificationEnvelope notification,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(notification);

        if (!channel.Settings.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            return DeliveryOutcome.Permanent("No SMTP host is configured for this channel.");
        }

        var port = channel.Settings.TryGetValue("port", out var portText)
            && int.TryParse(portText, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 587;

        var from = channel.Settings.TryGetValue("from", out var fromAddress) && !string.IsNullOrWhiteSpace(fromAddress)
            ? fromAddress
            : "airside@localhost";

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));

        try
        {
            message.To.Add(MailboxAddress.Parse(channel.Endpoint));
        }
        catch (ParseException)
        {
            return DeliveryOutcome.Permanent($"'{channel.Endpoint}' is not a valid email address.");
        }

        // The instance name leads, because somebody running Airside on three
        // servers gets three sets of alerts and a subject line that does not say
        // which host is a subject line that has to be opened.
        message.Subject = $"[{notification.InstanceName}] {Prefix(notification.Level)}{notification.Title}";

        message.Body = new BodyBuilder
        {
            TextBody = BuildText(notification),
        }.ToMessageBody();

        using var client = new SmtpClient();

        try
        {
            // Auto rather than a fixed mode: 465 is implicit TLS and 587 is
            // STARTTLS, and picking the wrong one for a provider's port produces a
            // hang rather than an error. What is not negotiable is that plaintext
            // is never silently accepted — see the option below.
            var security = channel.Settings.TryGetValue("insecure", out var insecure)
                && string.Equals(insecure, "true", StringComparison.OrdinalIgnoreCase)
                    // Plaintext, and only because an operator asked for it by name.
                    // Some internal relays genuinely do not speak TLS; the
                    // password crosses the wire in the clear when they do not.
                    ? SecureSocketOptions.None
                    : SecureSocketOptions.Auto;

            await client.ConnectAsync(host, port, security, ct).ConfigureAwait(false);

            if (channel.Secret is { } password
                && channel.Settings.TryGetValue("username", out var username)
                && !string.IsNullOrWhiteSpace(username))
            {
                await client.AuthenticateAsync(username, password.Reveal(), ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

            return DeliveryOutcome.Delivered;
        }
        catch (AuthenticationException ex)
        {
            // Credentials will be wrong again next time. Retrying an SMTP login
            // also gets accounts locked, which turns a typo into an outage.
            logger.LogWarning(ex, "SMTP authentication failed for channel {Channel}", channel.Name);

            return DeliveryOutcome.Permanent($"The SMTP server rejected the credentials: {ex.Message}");
        }
        catch (SmtpCommandException ex) when (ex.StatusCode is SmtpStatusCode.MailboxUnavailable
            or SmtpStatusCode.MailboxNameNotAllowed or SmtpStatusCode.UserNotLocalWillForward)
        {
            return DeliveryOutcome.Permanent($"The server rejected the recipient: {ex.Message}");
        }
        catch (SmtpCommandException ex)
        {
            return DeliveryOutcome.Transient($"The SMTP server refused the message: {ex.Message}");
        }
        catch (SmtpProtocolException ex)
        {
            return DeliveryOutcome.Transient($"The SMTP conversation failed: {ex.Message}");
        }
        catch (SslHandshakeException ex)
        {
            // Almost always a self-signed certificate on an internal relay. Named,
            // because the alternative is an operator concluding the port is wrong.
            return DeliveryOutcome.Permanent(
                $"The TLS handshake with {host}:{port} failed: {ex.Message} If this is an internal relay "
                + "with a self-signed certificate, it needs a certificate this host trusts.");
        }
        catch (IOException ex)
        {
            return DeliveryOutcome.Transient($"The connection to {host}:{port} failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DeliveryOutcome.Transient($"{host}:{port} did not respond in time.");
        }
    }

    /// <summary>
    /// A word in the subject line, so severity survives a phone's lock screen.
    /// </summary>
    /// <remarks>
    /// Text rather than an emoji or a colour: this is the part of the message that
    /// gets truncated, forwarded, and read on a watch, and every one of those
    /// renders a word reliably.
    /// </remarks>
    private static string Prefix(NotificationLevel level) => level switch
    {
        NotificationLevel.Error => "ALERT: ",
        NotificationLevel.Warning => "Warning: ",
        _ => string.Empty,
    };

    /// <summary>
    /// Plain text only.
    /// </summary>
    /// <remarks>
    /// An HTML body would mean interpolating notification text — which includes
    /// hostnames and error messages from elsewhere — into markup, and the only
    /// thing that buys is a coloured banner in an alert nobody is reading for
    /// pleasure.
    /// </remarks>
    private static string BuildText(NotificationEnvelope notification)
    {
        var lines = new List<string>
        {
            notification.Body,
            string.Empty,
            $"Instance: {notification.InstanceName}",
            $"Severity: {notification.Level}",
        };

        if (notification.OccurrenceCount > 1)
        {
            lines.Add(
                $"Seen {notification.OccurrenceCount} times since "
                + notification.FirstSeenAt.ToString("u", CultureInfo.InvariantCulture));
        }

        if (notification.Code is not null)
        {
            lines.Add($"Code: {notification.Code}");
        }

        if (notification.DashboardUrl is not null)
        {
            lines.Add(string.Empty);
            lines.Add(notification.DashboardUrl);
        }

        return string.Join('\n', lines);
    }
}
