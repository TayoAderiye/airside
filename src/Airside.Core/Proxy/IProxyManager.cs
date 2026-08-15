namespace Airside.Core.Proxy;

/// <summary>
/// Drives the reverse proxy.
/// </summary>
/// <remarks>
/// <para>
/// Backed by Caddy's admin API — which is <em>not</em> at
/// <c>localhost:2019</c>. Caddy runs in <c>airside-proxy</c>; from the API
/// container, localhost is the API. The address is <c>airside-proxy:2019</c> over
/// the internal network.
/// </para>
/// <para>
/// That admin API is unauthenticated and can load configuration that executes
/// commands, so it is never published to the host and only the API container
/// shares a network with it. Treat this interface as a privileged surface.
/// </para>
/// </remarks>
public interface IProxyManager
{
    /// <summary>Creates or replaces the route for a hostname. Idempotent by <see cref="RouteSpec.Hostname"/>.</summary>
    Task UpsertRouteAsync(RouteSpec route, CancellationToken ct);

    /// <summary>
    /// Points an existing route at a new upstream. This is the zero-downtime
    /// cutover, and the reason rollback is a proxy change plus a container start
    /// rather than a rebuild.
    /// </summary>
    Task SwapUpstreamAsync(string hostname, UpstreamTarget upstream, CancellationToken ct);

    Task RemoveRouteAsync(string hostname, CancellationToken ct);

    Task<IReadOnlyList<RouteSpec>> ListRoutesAsync(CancellationToken ct);

    /// <summary>
    /// Reads certificate state. Caddy is the source of truth; Airside caches this
    /// so the UI can show issuer and expiry without a proxy round-trip, and so the
    /// expiry notification has something to compare against.
    /// </summary>
    Task<CertificateStatus?> GetCertificateAsync(string hostname, CancellationToken ct);

    Task<bool> IsAvailableAsync(CancellationToken ct);
}

public sealed record RouteSpec(
    string Hostname,
    UpstreamTarget Upstream,
    bool AutomaticTls = true);

/// <summary>
/// A container reachable on a shared network.
/// </summary>
/// <remarks>
/// Named rather than addressed by IP: the proxy joins each application's own
/// network, so upstreams resolve by container name and survive a container
/// replacement getting a new address.
/// </remarks>
public sealed record UpstreamTarget(string ContainerName, int Port);

public sealed record CertificateStatus(
    string Hostname,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool AutoRenew);
