using System.Net;
using System.Text.Json;
using Airside.Core.Proxy;
using Airside.Runtime.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Airside.Tests.Proxy;

/// <summary>
/// The Caddy admin client, driven against a recording handler.
/// </summary>
/// <remarks>
/// These assert the requests Airside actually sends. A route written to the wrong
/// admin path or with the wrong shape fails at runtime with a Caddy error that
/// names JSON rather than the mistake.
/// </remarks>
public class CaddyRouteTests
{
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

    [Fact]
    public void RouteIdsAreDerivedFromTheHostname()
    {
        // The id becomes a URL path segment, so it must contain nothing that
        // would change the path's meaning.
        Assert.Equal("airside-route-app-example-com", CaddyProxyManager.RouteId("app.example.com"));
        Assert.Equal("airside-route-app-example-com", CaddyProxyManager.RouteId("APP.EXAMPLE.COM"));
        Assert.Matches("^[a-z0-9-]+$", CaddyProxyManager.RouteId("weird_host.example.com"));
    }

    [Fact]
    public async Task UpsertPatchesAnExistingRouteInPlace()
    {
        // PATCH replaces the object atomically. Delete-then-add would leave a
        // window in which the hostname has no route, which during a deployment
        // cutover is a visible outage.
        var (proxy, handler) = Build((HttpStatusCode.OK, "{}"));

        await proxy.UpsertRouteAsync(
            new RouteSpec("app.example.com", new UpstreamTarget("airside-app-web-abc", 8080)),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/id/airside-route-app-example-com", request.Path);
    }

    [Fact]
    public async Task UpsertAppendsWhenNoRouteExistsYet()
    {
        var (proxy, handler) = Build(
            (HttpStatusCode.InternalServerError, "unknown object id"),
            (HttpStatusCode.OK, "{}"));

        await proxy.UpsertRouteAsync(
            new RouteSpec("app.example.com", new UpstreamTarget("airside-app-web-abc", 8080)),
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("/config/apps/http/servers/airside/routes", handler.Requests[1].Path);
    }

    [Fact]
    public async Task TheRouteCarriesTheHostMatcherUpstreamAndTerminalFlag()
    {
        var (proxy, handler) = Build((HttpStatusCode.OK, "{}"));

        await proxy.UpsertRouteAsync(
            new RouteSpec("app.example.com", new UpstreamTarget("airside-app-web-abc", 8080)),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var root = body.RootElement;

        Assert.Equal("airside-route-app-example-com", root.GetProperty("@id").GetString());
        Assert.Equal("app.example.com", root.GetProperty("match")[0].GetProperty("host")[0].GetString());

        var handle = root.GetProperty("handle")[0];
        Assert.Equal("reverse_proxy", handle.GetProperty("handler").GetString());
        Assert.Equal("airside-app-web-abc:8080", handle.GetProperty("upstreams")[0].GetProperty("dial").GetString());

        // Terminal stops Caddy evaluating later routes once this host matches, so
        // one application cannot receive another's traffic through ordering.
        Assert.True(root.GetProperty("terminal").GetBoolean());
    }

    [Fact]
    public async Task TheUpstreamIsAContainerNameNotAnAddress()
    {
        // A container replaced by a deployment gets a new IP. A route pinned to
        // an address would keep pointing at the container that has gone.
        var (proxy, handler) = Build((HttpStatusCode.OK, "{}"));

        await proxy.UpsertRouteAsync(
            new RouteSpec("app.example.com", new UpstreamTarget("airside-app-web-abc", 8080)),
            CancellationToken.None);

        var dial = JsonDocument.Parse(handler.Requests[0].Body!)
            .RootElement.GetProperty("handle")[0].GetProperty("upstreams")[0].GetProperty("dial").GetString();

        Assert.Equal("airside-app-web-abc:8080", dial);
        Assert.DoesNotMatch(@"^\d+\.\d+\.\d+\.\d+", dial!);
    }

    [Fact]
    public async Task SwappingAnUpstreamIsTheSameCallAsUpserting()
    {
        // One code path, so a cutover and an initial bind cannot disagree about
        // what a route looks like.
        var (patchProxy, patchHandler) = Build((HttpStatusCode.OK, "{}"));
        await patchProxy.SwapUpstreamAsync(
            "app.example.com", new UpstreamTarget("airside-app-web-new", 8080), CancellationToken.None);

        var (upsertProxy, upsertHandler) = Build((HttpStatusCode.OK, "{}"));
        await upsertProxy.UpsertRouteAsync(
            new RouteSpec("app.example.com", new UpstreamTarget("airside-app-web-new", 8080)),
            CancellationToken.None);

        Assert.Equal(upsertHandler.Requests[0].Path, patchHandler.Requests[0].Path);
        Assert.Equal(upsertHandler.Requests[0].Body, patchHandler.Requests[0].Body);
    }

    [Fact]
    public async Task UpsertIsIdempotent()
    {
        var (proxy, handler) = Build((HttpStatusCode.OK, "{}"), (HttpStatusCode.OK, "{}"));
        var spec = new RouteSpec("app.example.com", new UpstreamTarget("airside-app-web-abc", 8080));

        await proxy.UpsertRouteAsync(spec, CancellationToken.None);
        await proxy.UpsertRouteAsync(spec, CancellationToken.None);

        // Same path, same body, both times — no accumulation, no duplicate route.
        Assert.Equal(handler.Requests[0].Path, handler.Requests[1].Path);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public async Task RemovingARouteThatIsAlreadyGoneSucceeds()
    {
        // Reconciliation and an explicit delete can race, and the outcome either
        // way is the route being absent — which is what was asked for.
        var (proxy, _) = Build((HttpStatusCode.NotFound, "unknown object id"));

        await proxy.RemoveRouteAsync("app.example.com", CancellationToken.None);
    }

    [Fact]
    public async Task ARejectedRouteThrowsRatherThanBeingIgnored()
    {
        var (proxy, _) = Build(
            (HttpStatusCode.InternalServerError, "no id"),
            (HttpStatusCode.BadRequest, "invalid route"));

        await Assert.ThrowsAsync<ProxyUnavailableException>(() =>
            proxy.UpsertRouteAsync(
                new RouteSpec("app.example.com", new UpstreamTarget("x", 1)), CancellationToken.None));
    }

    [Fact]
    public async Task ListingReturnsOnlyAirsideRoutes()
    {
        // Anything a human added to the proxy by hand is left alone rather than
        // reconciled away.
        var (proxy, _) = Build((HttpStatusCode.OK, """
        [
          {"@id":"airside-route-a-example-com","match":[{"host":["a.example.com"]}],
           "handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"airside-app-a-1:8080"}]}]},
          {"@id":"someone-elses-route","match":[{"host":["b.example.com"]}],
           "handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"other:80"}]}]}
        ]
        """));

        var routes = await proxy.ListRoutesAsync(CancellationToken.None);

        var route = Assert.Single(routes);
        Assert.Equal("a.example.com", route.Hostname);
        Assert.Equal("airside-app-a-1", route.Upstream.ContainerName);
        Assert.Equal(8080, route.Upstream.Port);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);

    private sealed class RecordingHandler(params (HttpStatusCode Status, string Body)[] responses)
        : HttpMessageHandler
    {
        private int _index;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, body));

            var (status, content) = _index < responses.Length
                ? responses[_index++]
                : (HttpStatusCode.OK, "{}");

            return new HttpResponseMessage(status) { Content = new StringContent(content) };
        }
    }
}
