using System.Net;
using Airside.Runtime.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Airside.Tests.Proxy;

/// <summary>
/// That withdrawing the fallback route is quiet once it has been withdrawn.
/// </summary>
/// <remarks>
/// <para>
/// Reconciliation calls this every two minutes for the life of any instance that
/// has a dashboard domain. Caddy answers a delete for an absent <c>@id</c> with a
/// 404 and logs it at <c>error</c> level, so deleting unconditionally wrote about
/// seven hundred error lines a day into the proxy log — every one of them
/// reporting that something Airside wanted gone was already gone.
/// </para>
/// <para>
/// Nothing broke, which is why it survived: the operator simply learned that the
/// proxy log is full of red. That is the actual damage, and it is only visible
/// from the log, never from the behaviour.
/// </para>
/// </remarks>
public class FallbackWithdrawalTests
{
    private const string RoutesPath = "/config/apps/http/servers/airside/routes";

    [Fact]
    public async Task AnAbsentFallbackIsNotDeletedAgain()
    {
        // The steady state on any instance with a dashboard domain, which is
        // every instance past its first few minutes.
        var (proxy, handler) = Build((HttpStatusCode.OK, """
        [
          {"@id":"airside-route-panel-example-com","match":[{"host":["panel.example.com"]}],
           "handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"airside-ui:3000"}]}]}
        ]
        """));

        await proxy.RemoveFallbackRouteAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(RoutesPath, request.Path);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task APresentFallbackIsStillDeleted()
    {
        // The check must not have made withdrawal conditional in practice. A
        // fallback left in place keeps serving the dashboard on the bare IP and
        // on every other hostname pointed at the host, which is the reason it is
        // withdrawn at all.
        var (proxy, handler) = Build(
            (HttpStatusCode.OK, $$"""
            [
              {"@id":"{{CaddyProxyManager.FallbackRouteId}}",
               "handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"airside-ui:3000"}]}]}
            ]
            """),
            (HttpStatusCode.OK, "{}"));

        await proxy.RemoveFallbackRouteAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal($"/id/{CaddyProxyManager.FallbackRouteId}", handler.Requests[1].Path);
    }

    [Fact]
    public async Task AnUnreadableConfigurationStillAttemptsTheDelete()
    {
        // Failing to read the routes is not evidence the fallback is absent.
        // Skipping the delete on a proxy that is merely slow to answer would
        // leave the catch-all serving the dashboard on every hostname — a real
        // problem traded for a cosmetic one.
        var (proxy, handler) = Build(
            (HttpStatusCode.ServiceUnavailable, "unavailable"),
            (HttpStatusCode.OK, "{}"));

        await proxy.RemoveFallbackRouteAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
    }

    [Fact]
    public async Task ADeleteThatLosesTheRaceIsNotAFailure()
    {
        // Between the check and the delete, another pass or an operator with curl
        // can remove it. The end state is the same, so a 404 here is success.
        var (proxy, _) = Build(
            (HttpStatusCode.OK, $$"""
            [{"@id":"{{CaddyProxyManager.FallbackRouteId}}","handle":[]}]
            """),
            (HttpStatusCode.NotFound, "unknown object ID"));

        await proxy.RemoveFallbackRouteAsync(CancellationToken.None);
    }

    private static (CaddyProxyManager Proxy, RecordingHandler Handler) Build(
        params (HttpStatusCode Status, string Body)[] responses)
    {
        var handler = new RecordingHandler(responses);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://airside-proxy:2019") };

        return (
            new CaddyProxyManager(
                client,
                Options.Create(new CaddyOptions()),
                NullLogger<CaddyProxyManager>.Instance),
            handler);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path);

    private sealed class RecordingHandler(params (HttpStatusCode Status, string Body)[] responses)
        : HttpMessageHandler
    {
        private int _next;

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath));

            var (status, body) = _next < responses.Length
                ? responses[_next++]
                : (HttpStatusCode.OK, "{}");

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
