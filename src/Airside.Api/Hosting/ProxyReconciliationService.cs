using Airside.Core.Domains;
using Airside.Core.Proxy;
using Airside.Runtime.Jobs;

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

            // The skip list has to match the set of non-Automatic domains exactly.
            // A hostname left on it after being switched back to Automatic would
            // never get a certificate, with nothing to explain why.
            var skip = await store.ListAutomaticHttpsSkipAsync(ct).ConfigureAwait(false);
            await proxy.SetAutomaticHttpsSkipAsync(skip, ct).ConfigureAwait(false);

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
