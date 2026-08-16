using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Airside.Core.Domains;
using Airside.Core.Proxy;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Proxy;

public sealed class CaddyOptions
{
    public const string Section = "Airside:Proxy";

    /// <summary>
    /// Caddy's admin API.
    /// </summary>
    /// <remarks>
    /// Not <c>localhost:2019</c>. Caddy runs in its own container, so from the API
    /// container localhost is the API. This address is reachable only over the
    /// internal network, and the port is never published to the host — the admin
    /// API is unauthenticated and can load configuration that executes commands,
    /// so exposing it would be equivalent to handing over the machine.
    /// </remarks>
    public string AdminAddress { get; set; } = "http://airside-proxy:2019";

    /// <summary>The server name in Caddy's config that Airside owns.</summary>
    public string ServerName { get; set; } = "airside";

    /// <summary>Contact address for ACME. Let's Encrypt uses it for expiry warnings.</summary>
    public string? AcmeEmail { get; set; }
}

/// <summary>
/// Drives Caddy through its admin API.
/// </summary>
/// <remarks>
/// Routes are addressed by <c>@id</c> rather than by array index. An index-based
/// update is a race with anything else touching the config and silently rewrites
/// the wrong route when the array shifts; an id is stable and makes upsert
/// genuinely idempotent.
/// </remarks>
public sealed class CaddyProxyManager(
    HttpClient http,
    Microsoft.Extensions.Options.IOptions<CaddyOptions> options,
    ILogger<CaddyProxyManager> logger) : IProxyManager
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CaddyOptions _options = options.Value;

    public const string RouteIdPrefix = "airside-route-";

    public static string RouteId(string hostname) =>
        // Only characters a Caddy @id and a URL path segment both tolerate.
        RouteIdPrefix + string.Concat(hostname.Select(c =>
            char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));

    public async Task UpsertRouteAsync(RouteSpec route, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);

        var id = RouteId(route.Hostname);
        var payload = BuildRoute(id, route);

        // PATCH replaces the object in place, so a cutover never leaves a window
        // where the hostname has no route at all. Delete-then-add would.
        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/id/{id}")
        {
            Content = JsonContent.Create(payload, options: Json),
        };

        var response = await http.SendAsync(patch, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Updated proxy route for {Hostname}", route.Hostname);
            return;
        }

        response.Dispose();

        // No such id yet: append it. Appending is the only case where the route
        // did not previously exist, so there is no window to protect.
        using var append = await http.PostAsJsonAsync(
            $"/config/apps/http/servers/{_options.ServerName}/routes",
            payload,
            Json,
            ct).ConfigureAwait(false);

        if (!append.IsSuccessStatusCode)
        {
            var body = await append.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProxyUnavailableException(
                $"Caddy rejected the route for {route.Hostname}: {append.StatusCode} {Trim(body)}");
        }

        logger.LogInformation("Added proxy route for {Hostname}", route.Hostname);
    }

    public Task SwapUpstreamAsync(string hostname, UpstreamTarget upstream, CancellationToken ct) =>
        // The same call. A route is fully described by its host and upstream, so
        // a cutover is an upsert — which keeps one code path instead of two that
        // could disagree about what a route looks like.
        UpsertRouteAsync(new RouteSpec(hostname, upstream), ct);

    public async Task RemoveRouteAsync(string hostname, CancellationToken ct)
    {
        using var response = await http
            .DeleteAsync(new Uri($"/id/{RouteId(hostname)}", UriKind.Relative), ct)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent: removing a route that is already gone is success, which
            // matters because reconciliation and an explicit delete can race.
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new ProxyUnavailableException($"Caddy rejected the route removal: {Trim(body)}");
    }

    public async Task<IReadOnlyList<RouteSpec>> ListRoutesAsync(CancellationToken ct)
    {
        using var response = await http
            .GetAsync(new Uri($"/config/apps/http/servers/{_options.ServerName}/routes", UriKind.Relative), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var routes = await response.Content
            .ReadFromJsonAsync<List<CaddyRoute>>(Json, ct)
            .ConfigureAwait(false) ?? [];

        return
        [
            .. routes
                .Where(r => r.Id?.StartsWith(RouteIdPrefix, StringComparison.Ordinal) == true)
                .Select(r => new RouteSpec(
                    r.Match?.FirstOrDefault()?.Host?.FirstOrDefault() ?? string.Empty,
                    ParseUpstream(r.Handle?.FirstOrDefault()?.Upstreams?.FirstOrDefault()?.Dial))),
        ];
    }

    private static UpstreamTarget ParseUpstream(string? dial)
    {
        if (string.IsNullOrEmpty(dial))
        {
            return new UpstreamTarget(string.Empty, 0);
        }

        var colon = dial.LastIndexOf(':');

        return colon > 0 && int.TryParse(dial[(colon + 1)..], out var port)
            ? new UpstreamTarget(dial[..colon], port)
            : new UpstreamTarget(dial, 0);
    }

    /// <summary>
    /// Certificate state is read from the connection, not from Caddy.
    /// </summary>
    /// <remarks>
    /// Caddy exposes no stable API for listing issued certificates, and reading
    /// its storage directly would couple Airside to an internal layout. Probing
    /// the endpoint reports what clients are actually served, which is the thing
    /// an operator wants to know — a certificate Caddy believes it has issued but
    /// is not presenting is precisely the failure worth catching.
    /// </remarks>
    public Task<CertificateStatus?> GetCertificateAsync(string hostname, CancellationToken ct) =>
        CertificateInspector.InspectAsync(hostname, ct);

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http
                .GetAsync(new Uri("/config/", UriKind.Relative), ct)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the route for a hostname, whose shape depends entirely on the mode.
    /// </summary>
    /// <remarks>
    /// A redirect carries no upstream, maintenance carries a static response
    /// instead of one, and External serves over plain HTTP because TLS ended at
    /// something in front of this host.
    /// </remarks>
    private static CaddyRoute BuildRoute(string id, RouteSpec route)
    {
        var handlers = new List<CaddyHandler>();

        // HSTS goes on first: a header handler has to run before the handler that
        // produces the response, or the response is already on its way out.
        if (route.Hsts is { } hsts && route.Mode != TlsMode.External)
        {
            handlers.Add(new CaddyHandler
            {
                Handler = "headers",
                Response = new CaddyHeaderOps
                {
                    Set = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                    {
                        ["Strict-Transport-Security"] = [hsts.ToHeaderValue()],
                    },
                },
            });
        }

        if (route.RedirectTo is { } target)
        {
            handlers.Add(new CaddyHandler
            {
                Handler = "static_response",

                // 308 rather than 301: it preserves the method and body, so a
                // POST to the www form does not silently become a GET.
                StatusCode = "308",
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Location"] = [$"https://{target}{{http.request.uri}}"],
                },
            });
        }
        else if (route.Maintenance)
        {
            // A stopped application still has a live hostname. Without this the
            // proxy has nowhere to send the request and returns a bare 502, which
            // tells a visitor nothing and an operator almost nothing.
            handlers.Add(new CaddyHandler
            {
                Handler = "static_response",
                StatusCode = "503",
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["text/html; charset=utf-8"],
                    ["Retry-After"] = ["120"],
                },
                Body = MaintenanceBody,
            });
        }
        else
        {
            handlers.Add(new CaddyHandler
            {
                Handler = "reverse_proxy",
                Upstreams = [new CaddyUpstream { Dial = $"{route.Upstream.ContainerName}:{route.Upstream.Port}" }],

                // Airside terminates TLS, so the upstream is spoken to over plain
                // HTTP on a private network. These tell the application what the
                // client actually asked for, which is otherwise lost.
                Headers = new CaddyProxyHeaders
                {
                    Request = new CaddyHeaderOps
                    {
                        Set = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                        {
                            ["X-Forwarded-Proto"] = [route.Mode == TlsMode.External ? "{http.request.header.X-Forwarded-Proto}" : "https"],
                            ["X-Forwarded-Host"] = ["{http.request.host}"],
                        },
                    },
                },
            });
        }

        return new CaddyRoute
        {
            Id = id,
            Match = [new CaddyMatch { Host = [route.Hostname] }],
            Handle = handlers,

            // Terminal stops Caddy evaluating later routes once this host matches,
            // so one application cannot receive another's traffic because of
            // ordering.
            Terminal = true,
        };
    }

    private const string MaintenanceBody =
        "<!doctype html><meta charset=utf-8><title>Temporarily unavailable</title>"
        + "<style>body{font-family:system-ui,sans-serif;margin:0;display:grid;place-items:center;"
        + "min-height:100vh;background:#0f1115;color:#e6e8eb}main{text-align:center;max-width:32rem;"
        + "padding:2rem}h1{font-weight:600;font-size:1.5rem;margin:0 0 .5rem}p{color:#9aa4b2;margin:0}</style>"
        + "<main><h1>Temporarily unavailable</h1>"
        + "<p>This site is not running at the moment. Please try again shortly.</p></main>";

    public async Task<IReadOnlyList<ObservedRoute>> ListAllRoutesAsync(CancellationToken ct)
    {
        using var response = await http
            .GetAsync(new Uri($"/config/apps/http/servers/{_options.ServerName}/routes", UriKind.Relative), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var routes = await response.Content
            .ReadFromJsonAsync<List<CaddyRoute>>(Json, ct)
            .ConfigureAwait(false) ?? [];

        return
        [
            .. routes.Select(r => new ObservedRoute(
                r.Id ?? string.Empty,
                r.Match?.FirstOrDefault()?.Host?.FirstOrDefault() ?? string.Empty,
                ParseUpstream(r.Handle?.Find(h => h.Handler == "reverse_proxy")?.Upstreams?.FirstOrDefault()?.Dial),
                r.Id?.StartsWith(RouteIdPrefix, StringComparison.Ordinal) == true)),
        ];
    }

    /// <summary>
    /// Hands Caddy a certificate to hold in memory.
    /// </summary>
    /// <remarks>
    /// Loaded through <c>/load</c> against the TLS app's certificate pool rather
    /// than written to disk, so replacement takes effect on the next handshake
    /// with no restart and no dropped connection.
    /// </remarks>
    public async Task LoadCertificateAsync(ManualCertificate certificate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var payload = new CaddyLoadedCertificate
        {
            // Both, and they are not interchangeable: "@id" is what makes the
            // entry addressable at /id/... for replacement, while "tags" is how
            // Caddy selects a certificate when serving. Setting only tags — which
            // is the obvious-looking choice — leaves the unload below returning
            // 404 for ever, so every replacement quietly adds another certificate
            // to the pool instead of superseding the old one.
            Id = CertificateTag(certificate.Hostname),
            Certificate = certificate.CertificateChainPem,
            Key = certificate.PrivateKeyPem.Reveal(),
            Tags = [CertificateTag(certificate.Hostname)],
        };

        await EnsureTlsPathsAsync(ct).ConfigureAwait(false);

        // Replace by tag rather than append: uploading a renewal twice must not
        // leave the old certificate in the pool, where Caddy might still pick it.
        await UnloadCertificateAsync(certificate.Hostname, ct).ConfigureAwait(false);

        using var response = await http.PostAsJsonAsync(
            "/config/apps/tls/certificates/load_pem", payload, Json, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProxyUnavailableException(
                $"Caddy rejected the certificate for {certificate.Hostname}: {response.StatusCode} {Trim(body)}");
        }

        logger.LogInformation("Loaded a manual certificate for {Hostname}", certificate.Hostname);
    }

    public async Task UnloadCertificateAsync(string hostname, CancellationToken ct)
    {
        using var response = await http
            .DeleteAsync(new Uri($"/id/{CertificateTag(hostname)}", UriKind.Relative), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogWarning("Caddy would not unload the certificate for {Hostname}: {Body}", hostname, Trim(body));
        }
    }

    /// <summary>
    /// Replaces the whole TLS policy for non-Automatic hostnames.
    /// </summary>
    /// <remarks>
    /// Whole-list writes rather than incremental edits, because each list has to
    /// match its set of domains exactly. Adding without removing would leave a
    /// hostname skipped after it was switched back to Automatic, and it would then
    /// never get a certificate with nothing to explain why.
    /// </remarks>
    public async Task ApplyTlsPolicyAsync(TlsPolicySet policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var automatic = new CaddyAutomaticHttps
        {
            Skip = [.. policy.SkipEntirely],
            SkipCertificates = [.. policy.SkipCertificates],
        };

        using var response = await http.PostAsJsonAsync(
            $"/config/apps/http/servers/{_options.ServerName}/automatic_https", automatic, Json, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProxyUnavailableException(
                $"Caddy rejected the automatic-HTTPS policy: {response.StatusCode} {Trim(body)}");
        }

        await EnsureTlsPathsAsync(ct).ConfigureAwait(false);

        // The automation block is replaced even when empty, so a hostname that
        // stops being Internal stops being issued for by the local CA.
        var automation = new CaddyAutomation
        {
            Policies = policy.Internal.Count == 0
                ? []
                :
                [
                    new CaddyAutomationPolicy
                    {
                        Subjects = [.. policy.Internal],
                        Issuers = [new CaddyIssuer { Module = "internal" }],
                    },
                ],
        };

        using var automationResponse = await http.PostAsJsonAsync(
            "/config/apps/tls/automation", automation, Json, ct).ConfigureAwait(false);

        if (!automationResponse.IsSuccessStatusCode)
        {
            var body = await automationResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProxyUnavailableException(
                $"Caddy rejected the internal-issuer policy: {automationResponse.StatusCode} {Trim(body)}");
        }
    }

    /// <summary>Creates the tls app and its certificate pool if the running config has neither.</summary>
    /// <remarks>
    /// The bootstrap config declares only the http app, so the first manual
    /// certificate would otherwise POST into a path that does not exist and fail
    /// with a message about JSON rather than about certificates.
    /// </remarks>
    private async Task EnsureTlsPathsAsync(CancellationToken ct)
    {
        using var probe = await http
            .GetAsync(new Uri("/config/apps/tls/certificates/load_pem", UriKind.Relative), ct)
            .ConfigureAwait(false);

        if (probe.IsSuccessStatusCode)
        {
            return;
        }

        using var seed = await http.PostAsJsonAsync(
            "/config/apps/tls",
            new CaddyTlsApp { Certificates = new CaddyCertificates { LoadPem = [] } },
            Json,
            ct).ConfigureAwait(false);

        if (!seed.IsSuccessStatusCode)
        {
            var body = await seed.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProxyUnavailableException($"Caddy would not accept a TLS app: {Trim(body)}");
        }
    }

    public const string CertificateIdPrefix = "airside-cert-";

    public static string CertificateTag(string hostname) =>
        CertificateIdPrefix + string.Concat(hostname.Select(c =>
            char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));

    public async Task<IReadOnlyList<string>> ListLoadedCertificateIdsAsync(CancellationToken ct)
    {
        using var response = await http
            .GetAsync(new Uri("/config/apps/tls/certificates/load_pem", UriKind.Relative), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // No tls app yet, which is the same as holding no certificates.
            return [];
        }

        var loaded = await response.Content
            .ReadFromJsonAsync<List<CaddyLoadedCertificate>>(Json, ct)
            .ConfigureAwait(false) ?? [];

        return [.. loaded.Select(c => c.Id).OfType<string>()];
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private sealed class CaddyRoute
    {
        [JsonPropertyName("@id")]
        public string? Id { get; set; }

        public List<CaddyMatch>? Match { get; set; }

        public List<CaddyHandler>? Handle { get; set; }

        public bool Terminal { get; set; }
    }

    private sealed class CaddyMatch
    {
        public List<string>? Host { get; set; }
    }

    private sealed class CaddyHandler
    {
        public string? Handler { get; set; }

        public List<CaddyUpstream>? Upstreams { get; set; }

        /// <summary>
        /// Deliberately untyped: Caddy uses "headers" for two different shapes.
        /// </summary>
        /// <remarks>
        /// <c>reverse_proxy</c> nests <c>request</c> and <c>response</c> objects
        /// under it; <c>static_response</c> takes a flat name-to-values map. Two
        /// CLR properties cannot both serialise to one JSON name, so the shape is
        /// chosen where the handler is built.
        /// </remarks>
        public object? Headers { get; set; }

        [JsonPropertyName("status_code")]
        public string? StatusCode { get; set; }

        public string? Body { get; set; }

        [JsonPropertyName("response")]
        public CaddyHeaderOps? Response { get; set; }
    }

    private sealed class CaddyProxyHeaders
    {
        public CaddyHeaderOps? Request { get; set; }
    }

    private sealed class CaddyHeaderOps
    {
        public Dictionary<string, List<string>>? Set { get; set; }
    }

    private sealed class CaddyLoadedCertificate
    {
        [JsonPropertyName("@id")]
        public string? Id { get; set; }

        public string? Certificate { get; set; }

        public string? Key { get; set; }

        public List<string>? Tags { get; set; }
    }

    private sealed class CaddyAutomaticHttps
    {
        public List<string>? Skip { get; set; }

        [JsonPropertyName("skip_certificates")]
        public List<string>? SkipCertificates { get; set; }
    }

    private sealed class CaddyAutomation
    {
        public List<CaddyAutomationPolicy>? Policies { get; set; }
    }

    private sealed class CaddyAutomationPolicy
    {
        public List<string>? Subjects { get; set; }

        public List<CaddyIssuer>? Issuers { get; set; }
    }

    private sealed class CaddyIssuer
    {
        public string? Module { get; set; }
    }

    private sealed class CaddyTlsApp
    {
        public CaddyCertificates? Certificates { get; set; }
    }

    private sealed class CaddyCertificates
    {
        [JsonPropertyName("load_pem")]
        public List<CaddyLoadedCertificate>? LoadPem { get; set; }
    }

    private sealed class CaddyUpstream
    {
        public string? Dial { get; set; }
    }
}

public sealed class ProxyUnavailableException : Exception
{
    public ProxyUnavailableException()
    {
    }

    public ProxyUnavailableException(string message)
        : base(message)
    {
    }

    public ProxyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
