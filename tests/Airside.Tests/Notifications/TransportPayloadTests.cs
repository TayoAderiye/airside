using System.Net;
using System.Text.Json;
using Airside.Core.Common;
using Airside.Core.Notifications;
using Airside.Core.Operations;
using Airside.Runtime.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airside.Tests.Notifications;

/// <summary>
/// What each transport actually puts on the wire.
/// </summary>
/// <remarks>
/// The webhook body and its signature were verified against a real receiver, and
/// the signature recomputed independently. These pin the shapes so a change shows
/// up here rather than in somebody's incident channel.
/// </remarks>
public class TransportPayloadTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 16, 4, 0, 0, TimeSpan.Zero);

    private static NotificationEnvelope Envelope(
        NotificationLevel level = NotificationLevel.Error,
        int occurrences = 1) =>
        new(
            Guid.CreateVersion7(),
            level,
            "Certificate expiring",
            "app.example.com expires in 3 days.",
            "domain.certificate_expiring",
            "domain",
            Guid.CreateVersion7(),
            occurrences,
            When.AddDays(-2),
            When,
            "prod-1",
            "https://airside.example.com/notifications");

    private static (WebhookTransport Transport, Recorder Recorder) Webhook(HttpStatusCode status = HttpStatusCode.OK)
    {
        var recorder = new Recorder(status);

        return (
            new WebhookTransport(new HttpClient(recorder), NullLogger<WebhookTransport>.Instance),
            recorder);
    }

    private static ChannelTarget Target(ChannelKind kind, string? secret = null) =>
        new(Guid.CreateVersion7(), "ops", kind, "https://example.com/hook",
            secret is null ? null : new Secret(secret), new Dictionary<string, string>(StringComparer.Ordinal));

    [Fact]
    public async Task TheWebhookBodyCarriesEverythingAReceiverNeedsToAct()
    {
        var (transport, recorder) = Webhook();

        var outcome = await transport.SendAsync(Target(ChannelKind.Webhook), Envelope(), CancellationToken.None);

        Assert.True(outcome.Succeeded);

        var body = JsonDocument.Parse(recorder.LastBody!).RootElement;

        Assert.Equal("error", body.GetProperty("level").GetString());
        Assert.Equal("Certificate expiring", body.GetProperty("title").GetString());
        Assert.Equal("domain.certificate_expiring", body.GetProperty("code").GetString());

        // The instance name, because somebody running Airside on three servers
        // gets three sets of alerts and "a certificate is expiring" is useless
        // without knowing which host said so.
        Assert.Equal("prod-1", body.GetProperty("instance").GetString());
        Assert.Equal("domain", body.GetProperty("resource").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TheSignatureCoversTheTimestampAndTheBody()
    {
        // Signing the body alone would let a captured request be replayed for
        // ever. The timestamp is inside the signed material, not beside it, so it
        // cannot be edited without breaking the signature.
        var (transport, recorder) = Webhook();

        await transport.SendAsync(Target(ChannelKind.Webhook, "sign-me"), Envelope(), CancellationToken.None);

        var timestamp = recorder.LastHeaders!["X-Airside-Timestamp"];
        var signature = recorder.LastHeaders["X-Airside-Signature"];

        var expected = "sha256=" + Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("sign-me"),
                System.Text.Encoding.UTF8.GetBytes($"{timestamp}.{recorder.LastBody}")))
            .ToLowerInvariant();

        Assert.Equal(expected, signature);
    }

    [Fact]
    public async Task NoSecretMeansNoSignatureHeaders()
    {
        var (transport, recorder) = Webhook();

        await transport.SendAsync(Target(ChannelKind.Webhook), Envelope(), CancellationToken.None);

        Assert.DoesNotContain("X-Airside-Signature", recorder.LastHeaders!.Keys, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task OnlyFailuresThatMightChangeAreRetried(HttpStatusCode status, bool retryable)
    {
        // Retrying a 401 spends attempts to be told the same thing, and against a
        // signed webhook it looks like an attack. 429 is the exception: it is the
        // receiver asking for exactly that.
        var (transport, _) = Webhook(status);

        var outcome = await transport.SendAsync(Target(ChannelKind.Webhook), Envelope(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(retryable, outcome.Retryable);
    }

    [Fact]
    public async Task ARedirectIsRefusedRatherThanFollowed()
    {
        // A redirect target is chosen by the remote server and has not been
        // through the outbound guard — following one would walk the request to
        // wherever it liked, including the metadata service.
        var (transport, _) = Webhook(HttpStatusCode.Found);

        var outcome = await transport.SendAsync(Target(ChannelKind.Webhook), Envelope(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Retryable);
        Assert.Contains("redirect", outcome.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SlackPostsToTheSecretUrlAndNotTheDisplayEndpoint()
    {
        // For Slack the URL *is* the credential, so it is stored encrypted and the
        // endpoint column keeps only the host. Posting to the endpoint would hit a
        // host with no token path and get a 404 that reads as "Slack is down".
        var (webhook, recorder) = Webhook();
        var slack = new SlackTransport(webhook);

        await slack.SendAsync(
            new ChannelTarget(
                Guid.CreateVersion7(), "ops", ChannelKind.Slack, "hooks.slack.com",
                new Secret("https://hooks.slack.com/services/T0/B0/token"),
                new Dictionary<string, string>(StringComparer.Ordinal)),
            Envelope(),
            CancellationToken.None);

        Assert.Equal("https://hooks.slack.com/services/T0/B0/token", recorder.LastUrl);
    }

    [Fact]
    public async Task TheSlackMessageIsStructuredAndKeepsAFallbackText()
    {
        var (webhook, recorder) = Webhook();
        var slack = new SlackTransport(webhook);

        await slack.SendAsync(
            Target(ChannelKind.Slack, "https://hooks.slack.com/services/T0/B0/token"),
            Envelope(occurrences: 14),
            CancellationToken.None);

        var body = JsonDocument.Parse(recorder.LastBody!).RootElement;

        // Slack shows "text" in the push notification and in clients that do not
        // render blocks. Omitting it produces an alert that says "New message".
        Assert.Contains("Certificate expiring", body.GetProperty("text").GetString()!, StringComparison.Ordinal);

        var blocks = body.GetProperty("blocks");
        Assert.Equal("header", blocks[0].GetProperty("type").GetString());
        Assert.Equal("section", blocks[1].GetProperty("type").GetString());

        // Dedupe means one message stands for many observations, and the count is
        // the difference between a blip and a pattern.
        var context = blocks[2].GetProperty("elements")[0].GetProperty("text").GetString()!;
        Assert.Contains("seen 14 times", context, StringComparison.Ordinal);
        Assert.Contains("prod-1", context, StringComparison.Ordinal);
    }

    private sealed class Recorder(HttpStatusCode status) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public string? LastUrl { get; private set; }

        public Dictionary<string, string>? LastHeaders { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            LastHeaders = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.Ordinal);

            return new HttpResponseMessage(status) { Content = new StringContent("ok") };
        }
    }
}
