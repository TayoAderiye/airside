using Airside.Core.Containers;
using Airside.Core.Domains;
using Airside.Core.Naming;
using Airside.Core.Proxy;
using Airside.Data;
using Airside.Runtime.Jobs;
using Airside.Runtime.Proxy;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Hosting;

/// <summary>
/// Puts the proxy's routes back in step with the database.
/// </summary>
/// <remarks>
/// <para>
/// Routes added through Caddy's admin API do not survive the proxy container
/// being replaced — an update, a restart, or a host reboot brings it back with
/// whatever its bootstrap config said, which is nothing. Without this, every
/// domain would silently stop resolving after the first proxy restart and the
/// only symptom would be 404s.
/// </para>
/// <para>
/// The two halves of what this does are deliberately different, and the
/// distinction matters:
/// </para>
/// <para>
/// <b>Routes Airside owns are re-asserted.</b> That is not overriding an
/// administrator, it is restoring known-good state to a component whose runtime
/// config is not durable. Reporting drift and waiting for a human would mean a
/// proxy restart takes every site down until somebody notices and clicks
/// something.
/// </para>
/// <para>
/// <b>Routes Airside does not own are reported and never touched.</b> Silent
/// remediation on a system with this much reach is how you delete something an
/// administrator set up deliberately. A route without Airside's id prefix was put
/// there by a person, and the correct response is to say so.
/// </para>
/// </remarks>
public sealed class ProxyReconciliationService(
    IServiceScopeFactory scopeFactory,
    IProxyManager proxy,
    IContainerRuntime runtime,
    TimeProvider timeProvider,
    ILogger<ProxyReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>Foreign routes reported once each, not every two minutes for ever.</summary>
    private readonly HashSet<string> _reportedForeign = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);

        do
        {
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            if (!await proxy.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                // Normal during startup and during a proxy update. Reconciliation
                // is idempotent, so skipping a pass costs nothing.
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDomainStore>();

            var wanted = await store.ListLiveAsync(ct).ConfigureAwait(false);
            var actual = await proxy.ListAllRoutesAsync(ct).ConfigureAwait(false);

            var routable = wanted.Where(d => d.CurrentContainerName is not null).ToList();

            // Before any route is asserted. A route naming an upstream the proxy
            // has no network path to resolves to nothing and returns 502.
            await AttachNetworksAsync(routable, ct).ConfigureAwait(false);

            foreach (var domain in routable)
            {
                var existing = actual.FirstOrDefault(r =>
                    string.Equals(r.Hostname, domain.Hostname, StringComparison.OrdinalIgnoreCase));

                var upstream = new UpstreamTarget(domain.CurrentContainerName!, domain.ContainerPort);

                if (existing is not null
                    && existing.IsAirsideManaged
                    && string.Equals(existing.Upstream.ContainerName, upstream.ContainerName, StringComparison.Ordinal)
                    && existing.Upstream.Port == upstream.Port)
                {
                    continue;
                }

                if (existing is { IsAirsideManaged: false })
                {
                    // A hand-written route already claims this hostname. Replacing
                    // it would silently take over traffic the administrator routed
                    // somewhere on purpose.
                    ReportForeign(existing, $"it claims '{domain.Hostname}', which Airside also has a domain for");
                    continue;
                }

                await proxy.UpsertRouteAsync(
                    new RouteSpec(
                        domain.Hostname,
                        upstream,
                        domain.TlsMode,
                        domain.Hsts,
                        domain.RedirectTo,
                        Maintenance: !domain.ApplicationIsRunning),
                    ct).ConfigureAwait(false);

                logger.LogInformation(
                    "Reconciled the proxy route for {Hostname} to {Upstream}",
                    domain.Hostname, upstream.ContainerName);
            }

            // The fallback route, which is what makes a freshly installed box
            // reachable at all. Reconciled rather than installed once, because the
            // proxy container comes back with only what caddy.json gave it — and
            // an operator whose proxy restarted would otherwise find the dashboard
            // silently unreachable with nothing in any log to say why.
            var settings = await scope.ServiceProvider
                .GetRequiredService<AirsideDbContext>()
                .InstanceSettings.AsNoTracking()
                .FirstAsync(ct)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(settings.DashboardDomain))
            {
                await proxy.EnsureFallbackRouteAsync(
                    DashboardRoute.For(string.Empty), ct).ConfigureAwait(false);
            }
            else
            {
                // Reasserted, not assumed. The dashboard hostname lives in
                // InstanceSettings rather than in Domains, so the loop above —
                // which walks Domains — never sees it. A replaced proxy container
                // came back with the empty route list from caddy.json, the
                // fallback was withdrawn below because a domain exists, and the
                // result was a Caddy serving nothing at all: an empty 200 on port
                // 80 and a plain-HTTP listener on 443. The operator is locked out
                // of the only interface that could fix it.
                await proxy.UpsertRouteAsync(
                    DashboardRoute.For(settings.DashboardDomain), ct).ConfigureAwait(false);

                // The previous hostname keeps working until its grace period ends,
                // so DNS has time to move without the old address dying first.
                if (!string.IsNullOrEmpty(settings.PreviousDashboardDomain)
                    && settings.PreviousDashboardDomainUntil is { } until
                    && until > timeProvider.GetUtcNow().UtcDateTime)
                {
                    await proxy.UpsertRouteAsync(
                        DashboardRoute.For(settings.PreviousDashboardDomain), ct).ConfigureAwait(false);
                }

                // Withdrawn once there is a real dashboard domain. Left in place it
                // would keep serving the dashboard on the bare IP and on every
                // other hostname pointed at this host.
                await proxy.RemoveFallbackRouteAsync(ct).ConfigureAwait(false);
            }

            // The policy has to match the set of non-Automatic domains exactly. A
            // hostname left on a skip list after being switched back to Automatic
            // would never get a certificate, with nothing to explain why.
            var policy = await store.GetTlsPolicyAsync(ct).ConfigureAwait(false);
            await proxy.ApplyTlsPolicyAsync(policy, ct).ConfigureAwait(false);

            await ReloadCertificatesAsync(store, wanted, ct).ConfigureAwait(false);
            await RemoveOrphansAsync(actual, routable, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Reconciliation must never take the process down.
        catch (Exception ex)
        {
            logger.LogError(ex, "Proxy reconciliation failed; routes may be stale until the next pass");
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Rejoins the proxy to every application network it needs to reach.
    /// </summary>
    /// <remarks>
    /// Network attachments belong to a container, not to a name, so a replaced
    /// proxy comes back on the internal network and nothing else. Its routes are
    /// then reasserted perfectly and every one of them returns 502, because
    /// Airside's isolation is pairwise and the new container has no path to any
    /// application. Attaching is idempotent, so re-checking each pass is cheap and
    /// covers a deployment that created a network after the last one.
    /// </remarks>
    private async Task AttachNetworksAsync(List<DomainTarget> domains, CancellationToken ct)
    {
        if (domains.Count == 0)
        {
            return;
        }

        var container = await runtime.Containers
            .FindAsync(AirsideLabels.SystemContainers.Proxy, ct)
            .ConfigureAwait(false);

        if (container is null)
        {
            return;
        }

        foreach (var network in domains
            .Select(d => d.ApplicationNetworkName)
            .Distinct(StringComparer.Ordinal))
        {
            if (container.Networks.Contains(network, StringComparer.Ordinal))
            {
                continue;
            }

            await runtime.Networks.ConnectAsync(network, container.Id, ct).ConfigureAwait(false);

            logger.LogInformation("Reattached the proxy to {Network}", network);
        }
    }

    /// <summary>
    /// Puts uploaded certificates back into the proxy after it has been replaced.
    /// </summary>
    /// <remarks>
    /// An uploaded certificate lives only in Caddy's memory, so a replaced
    /// container comes back without it. That is the worst of the three states to
    /// be left in: the route is reasserted and the hostname is on
    /// <c>skip_certificates</c>, so Caddy has been told not to obtain a
    /// certificate and has none to serve. The site answers nothing at all on 443,
    /// and no log line connects that to a proxy restart twenty minutes earlier.
    /// </remarks>
    private async Task ReloadCertificatesAsync(
        IDomainStore store,
        IReadOnlyList<DomainTarget> domains,
        CancellationToken ct)
    {
        var manual = domains.Where(d => d.TlsMode == TlsMode.Manual).ToList();

        if (manual.Count == 0)
        {
            return;
        }

        var loaded = await proxy.ListLoadedCertificateIdsAsync(ct).ConfigureAwait(false);

        foreach (var domain in manual)
        {
            var id = CaddyProxyManager.CertificateTag(domain.Hostname);

            if (loaded.Contains(id, StringComparer.Ordinal))
            {
                continue;
            }

            var certificate = await store.GetManualCertificateAsync(domain.DomainId, ct).ConfigureAwait(false);

            if (certificate is null)
            {
                logger.LogWarning(
                    "{Hostname} is set to Manual TLS but has no stored certificate, so nothing can be "
                    + "served for it. Upload one.",
                    domain.Hostname);

                continue;
            }

            await proxy.LoadCertificateAsync(certificate, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Reloaded the uploaded certificate for {Hostname} into the proxy", domain.Hostname);
        }
    }

    /// <summary>
    /// Withdraws routes Airside created for domains that no longer exist.
    /// </summary>
    /// <remarks>
    /// Limited to Airside's own routes, and to those with no matching domain.
    /// These were created by this system for a domain that has since gone, so
    /// leaving them means traffic flowing to an upstream nobody authorised — and
    /// once the container behind it is replaced, a 502 for real visitors.
    /// </remarks>
    private async Task RemoveOrphansAsync(
        IReadOnlyList<ObservedRoute> actual,
        List<DomainTarget> routable,
        CancellationToken ct)
    {
        foreach (var route in actual)
        {
            var matched = routable.Exists(d =>
                string.Equals(d.Hostname, route.Hostname, StringComparison.OrdinalIgnoreCase));

            if (matched)
            {
                continue;
            }

            if (!route.IsAirsideManaged)
            {
                ReportForeign(route, "Airside did not create it and will not change it");
                continue;
            }

            await proxy.RemoveRouteAsync(route.Hostname, ct).ConfigureAwait(false);
            logger.LogWarning("Removed an Airside proxy route with no matching domain: {Hostname}", route.Hostname);
        }
    }

    private void ReportForeign(ObservedRoute route, string reason)
    {
        if (!_reportedForeign.Add(route.RouteId))
        {
            return;
        }

        logger.LogWarning(
            "Proxy drift: the route {RouteId} for {Hostname} is not managed by Airside — {Reason}.",
            route.RouteId, route.Hostname, reason);
    }
}
