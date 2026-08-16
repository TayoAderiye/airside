using System.Net;
using System.Text.Json;
using Airside.Core.Domains;
using Airside.Core.Proxy;
using Airside.Runtime.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Airside.Tests.Proxy;

/// <summary>
/// The route Caddy is sent for each TLS mode and each routing behaviour.
/// </summary>
/// <remarks>
/// These shapes were checked against a running Caddy before being pinned here, so
/// a failure means Airside changed rather than that the assertion was guessed.
/// The mistakes they catch are quiet ones: a redirect that keeps an upstream, an
/// HSTS header ordered after the handler that already sent the response, or a
/// hostname that stays on the automatic-HTTPS path when it should not.
/// </remarks>
public class RouteShapeTests
{
    private static (CaddyProxyManager Proxy, RecordingHandler Handler) Build(
        params (HttpStatusCode Status, string Body)[] responses)
    {
        var handler = new RecordingHandler(responses);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://airside-proxy:2019") };

        return (
            new CaddyProxyManager(client, Options.Create(new CaddyOptions()), NullLogger<CaddyProxyManager>.Instance),
            handler);
    }

    private static async Task<JsonElement> UpsertAsync(RouteSpec spec)
    {
        var (proxy, handler) = Build((HttpStatusCode.OK, "{}"));
        await proxy.UpsertRouteAsync(spec, CancellationToken.None);

        return JsonDocument.Parse(handler.Requests[0].Body!).RootElement;
    }

    private static RouteSpec Basic(TlsMode mode = TlsMode.Automatic) =>
        new("app.example.com", new UpstreamTarget("airside-app-web-abc", 8080), mode);

    [Fact]
    public async Task AnHstsPolicyIsEmittedBeforeTheHandlerThatRespondsAsync()
    {
        // Ordering is the whole point. A header handler placed after reverse_proxy
        // never runs, and the site silently serves no HSTS while the setting reads
        // as enabled.
        var root = await UpsertAsync(Basic() with { Hsts = new HstsPolicy(31536000, true, false) });

        var handlers = root.GetProperty("handle");

        Assert.Equal("headers", handlers[0].GetProperty("handler").GetString());
        Assert.Equal("reverse_proxy", handlers[1].GetProperty("handler").GetString());

        Assert.Equal(
            "max-age=31536000; includeSubDomains",
            handlers[0].GetProperty("response").GetProperty("set")
                .GetProperty("Strict-Transport-Security")[0].GetString());
    }

    [Fact]
    public void PreloadIsOmittedWithoutIncludeSubdomains()
    {
        // Browsers ignore a preload directive that lacks includeSubDomains, so
        // emitting it would set a policy the user believes is active and no
        // browser honours.
        Assert.DoesNotContain("preload", new HstsPolicy(31536000, false, true).ToHeaderValue(), StringComparison.Ordinal);
        Assert.Contains("preload", new HstsPolicy(31536000, true, true).ToHeaderValue(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectCarriesNoUpstreamAsync()
    {
        var root = await UpsertAsync(Basic() with { RedirectTo = "example.com" });

        var handler = root.GetProperty("handle")[0];

        Assert.Equal("static_response", handler.GetProperty("handler").GetString());

        // 308 rather than 301: it preserves the method and body, so a POST to the
        // www form does not silently become a GET at the apex.
        Assert.Equal("308", handler.GetProperty("status_code").GetString());
        Assert.Contains(
            "https://example.com",
            handler.GetProperty("headers").GetProperty("Location")[0].GetString(),
            StringComparison.Ordinal);

        Assert.False(root.GetProperty("handle")[0].TryGetProperty("upstreams", out _));
    }

    [Fact]
    public async Task AStoppedApplicationServesAHoldingPageRatherThanABareGatewayErrorAsync()
    {
        var root = await UpsertAsync(Basic() with { Maintenance = true });

        var handler = root.GetProperty("handle")[0];

        Assert.Equal("static_response", handler.GetProperty("handler").GetString());
        Assert.Equal("503", handler.GetProperty("status_code").GetString());
        Assert.Contains(
            "Temporarily unavailable", handler.GetProperty("body").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalModeForwardsTheClientsOwnProtocolRatherThanClaimingHttpsAsync()
    {
        // TLS ended at something in front of this host, so Airside does not know
        // the request arrived over HTTPS. Asserting "https" here would make an
        // application generate https:// links for a plain-HTTP visitor.
        var external = await UpsertAsync(Basic(TlsMode.External));

        var proto = external.GetProperty("handle")[0]
            .GetProperty("headers").GetProperty("request").GetProperty("set")
            .GetProperty("X-Forwarded-Proto")[0].GetString();

        Assert.Equal("{http.request.header.X-Forwarded-Proto}", proto);

        // Airside terminates TLS itself in every other mode, so it can say so.
        var automatic = await UpsertAsync(Basic());

        Assert.Equal(
            "https",
            automatic.GetProperty("handle")[0].GetProperty("headers").GetProperty("request")
                .GetProperty("set").GetProperty("X-Forwarded-Proto")[0].GetString());
    }

    [Fact]
    public async Task ExternalModeDoesNotEmitHstsAsync()
    {
        // The proxy in front is what terminates TLS and is what should decide the
        // policy. Emitting it here would apply a year-long HTTPS-only rule from a
        // listener that is serving plain HTTP.
        var root = await UpsertAsync(Basic(TlsMode.External) with { Hsts = new HstsPolicy(31536000, true, false) });

        Assert.Equal("reverse_proxy", root.GetProperty("handle")[0].GetProperty("handler").GetString());
    }

    [Fact]
    public async Task TheSkipListIsWrittenWholesaleAsync()
    {
        // Whole-list, not incremental. A hostname left on the list after being
        // switched back to Automatic would never get a certificate, and nothing
        // would explain why.
        var (proxy, handler) = Build((HttpStatusCode.OK, "{}"));

        await proxy.SetAutomaticHttpsSkipAsync(["a.example.com", "b.example.com"], CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal("/config/apps/http/servers/airside/automatic_https", request.Path);

        var skip = JsonDocument.Parse(request.Body!).RootElement.GetProperty("skip");
        Assert.Equal(2, skip.GetArrayLength());
    }

    [Fact]
    public async Task ACertificateIsLoadedWithAnAddressableIdNotOnlyATagAsync()
    {
        // Verified against a running Caddy: DELETE /id/<tag> returns 404, because
        // tags are for selection and "@id" is what makes an entry addressable.
        // Without the id, every replacement would add another certificate to the
        // pool instead of superseding the old one.
        var (proxy, handler) = Build(
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, "{}"));

        await proxy.LoadCertificateAsync(
            new ManualCertificate("app.example.com", "chain", new Core.Common.Secret("key")),
            CancellationToken.None);

        var load = handler.Requests.Find(r => r.Path.EndsWith("load_pem", StringComparison.Ordinal)
            && r.Method == HttpMethod.Post && r.Body?.Contains("chain", StringComparison.Ordinal) == true);

        Assert.NotNull(load);

        var body = JsonDocument.Parse(load!.Body!).RootElement;
        Assert.Equal("airside-cert-app-example-com", body.GetProperty("@id").GetString());
        Assert.Equal("airside-cert-app-example-com", body.GetProperty("tags")[0].GetString());

        // And the old one is withdrawn by that id before the new one goes in.
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path == "/id/airside-cert-app-example-com");
    }

    [Fact]
    public async Task ListingAllRoutesDistinguishesAirsideRoutesFromHandWrittenOnesAsync()
    {
        // Reconciliation reasserts the first and only reports the second, so the
        // distinction has to survive the round trip.
        var (proxy, _) = Build((HttpStatusCode.OK, """
        [
          {"@id":"airside-route-a-example-com","match":[{"host":["a.example.com"]}],
           "handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"airside-app-a-1:8080"}]}]},
          {"@id":"someone-elses-route","match":[{"host":["b.example.com"]}],
           "handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"other:80"}]}]}
        ]
        """));

        var routes = await proxy.ListAllRoutesAsync(CancellationToken.None);

        Assert.Equal(2, routes.Count);
        Assert.True(routes.Single(r => r.Hostname == "a.example.com").IsAirsideManaged);
        Assert.False(routes.Single(r => r.Hostname == "b.example.com").IsAirsideManaged);
    }

    [Fact]
    public async Task AnHstsRouteStillFindsItsUpstreamWhenListedAsync()
    {
        // The upstream is read from whichever handler is the reverse_proxy, not
        // from the first one — which is the headers handler once HSTS is on.
        var (proxy, _) = Build((HttpStatusCode.OK, """
        [
          {"@id":"airside-route-a-example-com","match":[{"host":["a.example.com"]}],
           "handle":[
             {"handler":"headers","response":{"set":{"Strict-Transport-Security":["max-age=1"]}}},
             {"handler":"reverse_proxy","upstreams":[{"dial":"airside-app-a-1:8080"}]}]}
        ]
        """));

        var route = Assert.Single(await proxy.ListAllRoutesAsync(CancellationToken.None));

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
