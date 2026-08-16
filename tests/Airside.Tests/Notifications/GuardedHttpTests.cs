using System.Net;
using System.Net.Sockets;
using Airside.Runtime.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airside.Tests.Notifications;

/// <summary>
/// The outbound guard as it actually behaves on a socket.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests cover which addresses are refused. These cover the part that
/// cannot be checked by inspecting a URL: that the refusal happens when the
/// connection is made, so a name resolving to a private address is stopped even
/// though the string looked ordinary.
/// </para>
/// <para>
/// A real listener is started, because "the request failed" is not the assertion —
/// the assertion is that it failed <em>for the right reason</em>, and against a
/// socket that would otherwise have answered.
/// </para>
/// </remarks>
public class GuardedHttpTests
{
    private static HttpClient Guarded(bool allowPrivate = false) =>
        new(GuardedHttp.CreateHandler(allowPrivate, NullLogger.Instance));

    [Fact]
    public async Task ARequestToALiveLoopbackListenerIsRefused()
    {
        // The listener answers. Without the guard this request would succeed, and
        // on a real host the thing answering could be Airside's own API or the
        // proxy's unauthenticated admin port.
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        try
        {
            using var client = Guarded();

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => client.GetAsync(new Uri($"http://127.0.0.1:{port}/"), CancellationToken.None));

            Assert.Contains("loopback", Flatten(error), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TheCloudMetadataAddressIsRefused()
    {
        using var client = Guarded();

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync(
                new Uri("http://169.254.169.254/latest/meta-data/"), CancellationToken.None));

        Assert.Contains("link-local", Flatten(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostnameResolvingToLoopbackIsRefusedEvenThoughTheUrlLooksOrdinary()
    {
        // The case a URL check cannot catch. "localhost" is the tame version of
        // an attacker-controlled name whose A record points at 127.0.0.1 — the URL
        // is unremarkable and only the resolved address gives it away.
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        try
        {
            using var client = Guarded();

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => client.GetAsync(new Uri($"http://localhost:{port}/"), CancellationToken.None));

            Assert.Contains("loopback", Flatten(error), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TheEscapeHatchDoesNotOpenLoopbackEvenOnALiveListener()
    {
        // The private-network switch is for an operator with a receiver on their
        // own network. It must not also open this host: a listener on loopback is
        // Airside's own API, or something else deliberately not exposed. Asserted
        // against a socket that would otherwise answer.
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        try
        {
            using var client = Guarded(allowPrivate: true);

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => client.GetAsync(new Uri($"http://127.0.0.1:{port}/"), CancellationToken.None));

            Assert.Contains("loopback", Flatten(error), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string Flatten(Exception error)
    {
        var text = error.Message;

        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += " " + inner.Message;
        }

        return text;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }
}

/// <summary>
/// That a refused destination is reported as configuration, not as weather.
/// </summary>
/// <remarks>
/// The guard throws from inside <c>ConnectCallback</c>, and HttpClient wraps
/// whatever comes out of there in an <see cref="HttpRequestException"/> — so the
/// obvious catch never sees it. A refused webhook was being classified as a
/// temporary network problem, retried five times, and used to mute the channel:
/// a configuration mistake reported as an outage, with the real cause buried.
/// </remarks>
public class BlockedDestinationClassificationTests
{
    [Fact]
    public async Task ARefusedDestinationIsPermanentRatherThanRetried()
    {
        var transport = new WebhookTransport(
            new HttpClient(GuardedHttp.CreateHandler(false, NullLogger.Instance)),
            NullLogger<WebhookTransport>.Instance);

        var outcome = await transport.SendAsync(
            new Airside.Core.Notifications.ChannelTarget(
                Guid.CreateVersion7(),
                "evil",
                Airside.Core.Notifications.ChannelKind.Webhook,
                "http://169.254.169.254/latest/meta-data/",
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            new Airside.Core.Notifications.NotificationEnvelope(
                Guid.CreateVersion7(),
                Airside.Core.Operations.NotificationLevel.Error,
                "t", "b", null, null, null, 1,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test", null),
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Retryable);
        Assert.Contains("link-local", outcome.Detail!, StringComparison.OrdinalIgnoreCase);
    }
}
