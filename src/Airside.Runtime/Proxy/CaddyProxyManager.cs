using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public static string RouteId(string hostname) =>
        // Only characters a Caddy @id and a URL path segment both tolerate.
        "airside-route-" + string.Concat(hostname.Select(c =>
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
                .Where(r => r.Id?.StartsWith("airside-route-", StringComparison.Ordinal) == true)
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

    private static CaddyRoute BuildRoute(string id, RouteSpec route) => new()
    {
        Id = id,
        Match = [new CaddyMatch { Host = [route.Hostname] }],
        Handle =
        [
            new CaddyHandler
            {
                Handler = "reverse_proxy",
                Upstreams = [new CaddyUpstream { Dial = $"{route.Upstream.ContainerName}:{route.Upstream.Port}" }],
            },
        ],

        // Terminal stops Caddy evaluating later routes once this host matches,
        // so one application cannot receive another's traffic because of
        // ordering.
        Terminal = true,
    };

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
