using System.Net;
using System.Text.Json;
using Airside.Core.Naming;
using Airside.Core.Proxy;
using Airside.Runtime.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Airside.Tests.Proxy;

/// <summary>
/// The dashboard hostname, which is two containers behind one name.
/// </summary>
/// <remarks>
/// <para>
/// The UI and the API ship separately, so one hostname has to reach both: the
/// API under <c>/api</c>, the dashboard everywhere else. Getting the split wrong
/// does not fail loudly — the dashboard's own API calls are answered with the
/// dashboard's HTML, which arrives as a 200 and dies in a JSON parser.
/// </para>
/// <para>
/// The route is asserted as the JSON that goes over the wire, because that is the
/// only part Caddy sees. A correct C# object serialised into a shape Caddy reads
/// differently is exactly the failure worth catching here.
/// </para>
/// </remarks>
public class DashboardRouteTests
{
    private static (CaddyProxyManager Proxy, RecordingHandler Handler) Build()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, "{}"));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://airside-proxy:2019") };

        return (
            new CaddyProxyManager(
                client,
                Options.Create(new CaddyOptions()),
                NullLogger<CaddyProxyManager>.Instance),
            handler);
    }

    private static async Task<JsonElement> RouteJsonAsync(RouteSpec spec)
    {
        var (proxy, handler) = Build();
        await proxy.UpsertRouteAsync(spec, CancellationToken.None);

        return JsonDocument.Parse(handler.Requests[0].Body!).RootElement.Clone();
    }

    [Fact]
    public async Task ApiPathsGoToTheApiAndEverythingElseGoesToTheDashboard()
    {
        var root = await RouteJsonAsync(DashboardRoute.For("panel.example.com"));

        var subroute = root.GetProperty("handle")[0];
        Assert.Equal("subroute", subroute.GetProperty("handler").GetString());

        var nested = subroute.GetProperty("routes");
        Assert.Equal(2, nested.GetArrayLength());

        var api = nested[0];
        Assert.Equal(
            DashboardRoute.ApiPaths,
            api.GetProperty("match")[0].GetProperty("path").EnumerateArray().Select(p => p.GetString()!).ToList());
        Assert.Equal(
            $"{AirsideLabels.SystemContainers.Api}:{DashboardRoute.ApiPort}",
            api.GetProperty("handle")[0].GetProperty("upstreams")[0].GetProperty("dial").GetString());

        // The last nested route carries no matcher at all. That is what makes it
        // the fallback rather than one more candidate.
        var fallback = nested[1];
        Assert.False(fallback.TryGetProperty("match", out _));
        Assert.Equal(
            $"{AirsideLabels.SystemContainers.Ui}:{DashboardRoute.UiPort}",
            fallback.GetProperty("handle")[0].GetProperty("upstreams")[0].GetProperty("dial").GetString());
    }

    [Fact]
    public async Task TheApiPathsAreMatchersNotBarePrefixes()
    {
        var root = await RouteJsonAsync(DashboardRoute.For("panel.example.com"));

        var paths = root.GetProperty("handle")[0].GetProperty("routes")[0]
            .GetProperty("match")[0].GetProperty("path")
            .EnumerateArray().Select(p => p.GetString()!).ToList();

        // Caddy matches a bare "/api" as that exact path and nothing beneath it,
        // so dropping the wildcard would route /api/v1/version to the dashboard
        // while /api itself still looked correct.
        Assert.Contains("/api/*", paths);
        Assert.DoesNotContain("/api", paths);
    }

    [Fact]
    public async Task OrdinaryApplicationRoutesAreUnchanged()
    {
        // The path split belongs to the dashboard alone. An application route
        // acquiring a subroute would mean every upstream moved one level deeper
        // than everything reading these routes expects.
        var root = await RouteJsonAsync(
            new RouteSpec("app.example.com", new UpstreamTarget("airside-app-web-abc", 8080)));

        var handle = root.GetProperty("handle")[0];

        Assert.Equal("reverse_proxy", handle.GetProperty("handler").GetString());
        Assert.Equal("airside-app-web-abc:8080", handle.GetProperty("upstreams")[0].GetProperty("dial").GetString());
    }

    [Fact]
    public async Task ReadingBackASplitRouteFindsTheDashboardNotAnEmptyUpstream()
    {
        // Reconciliation compares what Caddy holds against what it expects. If a
        // split route read back as an empty upstream it would look like drift on
        // every pass and be rewritten forever, since rewriting produces the same
        // shape again.
        var (proxy, _) = BuildLister($$"""
        [
          {"@id":"airside-route-panel-example-com","match":[{"host":["panel.example.com"]}],
           "handle":[{"handler":"subroute","routes":[
             {"match":[{"path":["/api/*"]}],
              "handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"airside-api:8080"}]}]},
             {"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"airside-ui:3000"}]}]}
           ]}]}
        ]
        """);

        var route = Assert.Single(await proxy.ListRoutesAsync(CancellationToken.None));

        Assert.Equal("panel.example.com", route.Hostname);
        Assert.Equal(AirsideLabels.SystemContainers.Ui, route.Upstream.ContainerName);
        Assert.Equal(DashboardRoute.UiPort, route.Upstream.Port);
    }


    [Fact]
    public async Task TheFallbackRouteCarriesNoHostMatcherAtAll()
    {
        // No matcher is what makes it answer on a bare IP, which is the only
        // address a freshly installed box has. A host matcher of any kind here
        // would leave the install serving a blank page — Caddy listening on 80
        // and matching nothing.
        var (proxy, handler) = Build();

        await proxy.EnsureFallbackRouteAsync(DashboardRoute.For(string.Empty), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var root = body.RootElement;

        Assert.False(root.TryGetProperty("match", out _));
        Assert.Equal(CaddyProxyManager.FallbackRouteId, root.GetProperty("@id").GetString());
    }

    [Fact]
    public async Task TheFallbackStillSplitsApiTrafficFromTheDashboard()
    {
        var (proxy, handler) = Build();

        await proxy.EnsureFallbackRouteAsync(DashboardRoute.For(string.Empty), CancellationToken.None);

        var nested = JsonDocument.Parse(handler.Requests[0].Body!)
            .RootElement.GetProperty("handle")[0].GetProperty("routes");

        Assert.Equal(
            $"{AirsideLabels.SystemContainers.Api}:{DashboardRoute.ApiPort}",
            nested[0].GetProperty("handle")[0].GetProperty("upstreams")[0].GetProperty("dial").GetString());
        Assert.Equal(
            $"{AirsideLabels.SystemContainers.Ui}:{DashboardRoute.UiPort}",
            nested[1].GetProperty("handle")[0].GetProperty("upstreams")[0].GetProperty("dial").GetString());
    }

    [Fact]
    public async Task RealRoutesAreInsertedAtTheFrontSoTheFallbackStaysLast()
    {
        // Caddy evaluates routes in array order. The fallback matches everything,
        // so anything appended after it is dead — an application domain bound
        // later would never be reached, and its traffic would silently arrive at
        // the dashboard instead.
        var handler = new RecordingHandler(
            (HttpStatusCode.InternalServerError, "unknown object id"),
            (HttpStatusCode.OK, "{}"));

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://airside-proxy:2019") };
        var proxy = new CaddyProxyManager(
            client, Options.Create(new CaddyOptions()), NullLogger<CaddyProxyManager>.Instance);

        await proxy.UpsertRouteAsync(
            new RouteSpec("app.example.com", new UpstreamTarget("airside-app-web-abc", 8080)),
            CancellationToken.None);

        var write = handler.Requests[1];

        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.EndsWith("/routes/0", write.Path, StringComparison.Ordinal);
    }

    private static (CaddyProxyManager Proxy, RecordingHandler Handler) BuildLister(string body)
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, body));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://airside-proxy:2019") };

        return (
            new CaddyProxyManager(
                client,
                Options.Create(new CaddyOptions()),
                NullLogger<CaddyProxyManager>.Instance),
            handler);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);

    private sealed class RecordingHandler(params (HttpStatusCode Status, string Body)[] responses)
        : HttpMessageHandler
    {
        private int _next;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                body));

            var (status, responseBody) = _next < responses.Length
                ? responses[_next++]
                : (HttpStatusCode.OK, "{}");

            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}
