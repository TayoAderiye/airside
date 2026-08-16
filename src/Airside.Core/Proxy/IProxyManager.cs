using Airside.Core.Common;
using Airside.Core.Domains;

namespace Airside.Core.Proxy;

/// <summary>
/// Drives the reverse proxy.
/// </summary>
/// <remarks>
/// <para>
/// Backed by Caddy's admin API — which is <em>not</em> at <c>localhost:2019</c>.
/// Caddy runs in <c>airside-proxy</c>; from the API container, localhost is the
/// API. The address is <c>airside-proxy:2019</c> over the internal network, and
/// the port is never published to the host.
/// </para>
/// <para>
/// That admin API is unauthenticated and can load configuration that executes
/// commands, so anyone who reaches it controls every route on the machine. Only
/// the API container shares a network with it, and an integration test asserts
/// that a workload network cannot. Treat this interface as a privileged surface.
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
    /// Everything Caddy is currently serving, including routes Airside did not create.
    /// </summary>
    /// <remarks>
    /// Reconciliation needs to tell "a route I own that has drifted" from "a route
    /// somebody added by hand", because the correct response differs: reassert the
    /// first, report and never touch the second.
    /// </remarks>
    Task<IReadOnlyList<ObservedRoute>> ListAllRoutesAsync(CancellationToken ct);

    /// <summary>
    /// Loads a user-supplied certificate into Caddy's in-memory pool.
    /// </summary>
    /// <remarks>
    /// A hot reload with no restart, so replacing a certificate causes no
    /// downtime. The caller is expected to verify afterwards by looking at what is
    /// actually being served — an admin API call returning 200 means Caddy
    /// accepted the configuration, not that the new certificate is on the wire.
    /// </remarks>
    Task LoadCertificateAsync(ManualCertificate certificate, CancellationToken ct);

    Task UnloadCertificateAsync(string hostname, CancellationToken ct);

    /// <summary>
    /// The ids of certificates Caddy currently holds.
    /// </summary>
    /// <remarks>
    /// Reconciliation needs this because an uploaded certificate lives only in the
    /// proxy's memory. A replaced proxy container comes back with its routes
    /// reasserted and its skip list correct — and no certificate, so a Manual
    /// hostname is told not to obtain one and has none to serve. Nothing on 443,
    /// and nothing in any log to say why.
    /// </remarks>
    Task<IReadOnlyList<string>> ListLoadedCertificateIdsAsync(CancellationToken ct);

    /// <summary>
    /// Tells Caddy how to treat each non-Automatic hostname.
    /// </summary>
    /// <remarks>
    /// Automatic HTTPS is opt-out in Caddy, so a hostname configured for manual,
    /// external, or internal TLS still gets an ACME attempt unless it is named
    /// here. Two issuers racing over one hostname burns quota and produces
    /// certificates that flap.
    /// </remarks>
    Task ApplyTlsPolicyAsync(TlsPolicySet policy, CancellationToken ct);

    /// <summary>
    /// Reads certificate state. Caddy is the source of truth; Airside caches this
    /// so the UI can show issuer and expiry without a proxy round-trip, and so the
    /// expiry notification has something to compare against.
    /// </summary>
    Task<CertificateStatus?> GetCertificateAsync(string hostname, CancellationToken ct);

    Task<bool> IsAvailableAsync(CancellationToken ct);
}

/// <param name="Mode">
/// Decides the shape of the route entirely: whether it terminates TLS, whether
/// Caddy is allowed to issue for it, and whether the listener serves plain HTTP.
/// </param>
/// <param name="RedirectTo">
/// Set when this hostname only redirects elsewhere — the apex/www pairing. The
/// route carries a redirect handler and no upstream.
/// </param>
/// <param name="Maintenance">
/// Serves a holding page instead of proxying. Used when the application is
/// stopped, so a live hostname returns something explicable rather than a raw
/// 502 from a proxy with nowhere to send the request.
/// </param>
public sealed record RouteSpec(
    string Hostname,
    UpstreamTarget Upstream,
    TlsMode Mode = TlsMode.Automatic,
    HstsPolicy? Hsts = null,
    string? RedirectTo = null,
    bool Maintenance = false);

/// <summary>
/// A route as Caddy currently has it.
/// </summary>
/// <param name="IsAirsideManaged">
/// False for anything without Airside's route-id prefix. Those are reported and
/// left alone: silent remediation on a system with this much reach is how you
/// delete something an administrator set up deliberately.
/// </param>
public sealed record ObservedRoute(string RouteId, string Hostname, UpstreamTarget Upstream, bool IsAirsideManaged);

/// <summary>
/// A container reachable on a shared network.
/// </summary>
/// <remarks>
/// Named rather than addressed by IP: the proxy joins each application's own
/// network, so upstreams resolve by container name and survive a container
/// replacement getting a new address.
/// </remarks>
public sealed record UpstreamTarget(string ContainerName, int Port);

/// <param name="Preload">
/// Submission to the browser preload list, which is effectively irreversible.
/// Removal takes months and requires valid HTTPS throughout; a user who enables
/// it and later needs plain HTTP has bricked the hostname in every major browser.
/// </param>
public sealed record HstsPolicy(int MaxAgeSeconds, bool IncludeSubdomains, bool Preload)
{
    public string ToHeaderValue()
    {
        var value = $"max-age={MaxAgeSeconds}";

        if (IncludeSubdomains)
        {
            value += "; includeSubDomains";
        }

        // Preload is only honoured alongside includeSubDomains and a long
        // max-age, so emitting it without them would be a directive browsers
        // ignore while the user believes it took effect.
        if (Preload && IncludeSubdomains)
        {
            value += "; preload";
        }

        return value;
    }
}

/// <param name="PrivateKeyPem">
/// Decrypted only for the moment it is handed to Caddy. Held as a
/// <see cref="Secret"/> so that logging or serialising the surrounding object
/// cannot leak it.
/// </param>
public sealed record ManualCertificate(string Hostname, string CertificateChainPem, Secret PrivateKeyPem);

/// <summary>
/// How Caddy should treat each hostname that is not <see cref="TlsMode.Automatic"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three lists rather than one, because Caddy's two skip settings mean genuinely
/// different things and using the wrong one produces a hostname that answers
/// nothing on 443. This was found by serving a real request, not by reading the
/// documentation:
/// </para>
/// <para>
/// <see cref="SkipEntirely"/> maps to <c>skip</c>, which turns automatic HTTPS off
/// altogether — no certificate management, no redirect, and <b>no TLS listener for
/// that host</b>. Right for <see cref="TlsMode.External"/>, where something in
/// front has already terminated TLS and this server should serve plain HTTP.
/// </para>
/// <para>
/// <see cref="SkipCertificates"/> maps to <c>skip_certificates</c>, which keeps
/// HTTPS switched on and only stops Caddy trying to obtain a certificate. Right
/// for <see cref="TlsMode.Manual"/>, where a certificate has been uploaded and
/// Caddy still has to terminate the connection with it. Using <c>skip</c> here
/// loads the certificate correctly and then never serves it.
/// </para>
/// <para>
/// <see cref="Internal"/> is not a skip at all: it is an automation policy naming
/// Caddy's own local CA as the issuer, so certificates are still managed — just
/// not by a public authority.
/// </para>
/// </remarks>
public sealed record TlsPolicySet(
    IReadOnlyCollection<string> SkipEntirely,
    IReadOnlyCollection<string> SkipCertificates,
    IReadOnlyCollection<string> Internal);

public sealed record CertificateStatus(
    string Hostname,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool AutoRenew);
