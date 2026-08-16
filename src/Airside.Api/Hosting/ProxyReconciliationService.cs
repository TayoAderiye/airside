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
/// So the database is the source of truth and this reasserts it: at startup, and
/// then on a timer. It also removes routes for domains that no longer exist,
/// because a stale route is traffic going somewhere nobody has authorised.
/// </para>
/// </remarks>
public sealed class ProxyReconciliationService(
    IServiceScopeFactory scopeFactory,
    IProxyManager proxy,
    TimeProvider timeProvider,
    ILogger<ProxyReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

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
            var actual = await proxy.ListRoutesAsync(ct).ConfigureAwait(false);

            var routable = wanted.Where(d => d.CurrentContainerName is not null).ToList();

            foreach (var domain in routable)
            {
                var existing = actual.FirstOrDefault(r =>
                    string.Equals(r.Hostname, domain.Hostname, StringComparison.OrdinalIgnoreCase));

                var upstream = new UpstreamTarget(domain.CurrentContainerName!, domain.ContainerPort);

                if (existing is not null
                    && string.Equals(existing.Upstream.ContainerName, upstream.ContainerName, StringComparison.Ordinal)
                    && existing.Upstream.Port == upstream.Port)
                {
                    continue;
                }

                await proxy.UpsertRouteAsync(new RouteSpec(domain.Hostname, upstream), ct).ConfigureAwait(false);

                logger.LogInformation(
                    "Reconciled the proxy route for {Hostname} to {Upstream}",
                    domain.Hostname, upstream.ContainerName);
            }

            foreach (var stale in actual.Where(r =>
                !routable.Exists(d => string.Equals(d.Hostname, r.Hostname, StringComparison.OrdinalIgnoreCase))))
            {
                // A route with no matching domain sends traffic somewhere nobody
                // has authorised, so it goes — unlike container drift, which is
                // only reported. The difference is that a route is Airside's own
                // configuration rather than a user's data.
                await proxy.RemoveRouteAsync(stale.Hostname, ct).ConfigureAwait(false);
                logger.LogWarning("Removed a proxy route with no matching domain: {Hostname}", stale.Hostname);
            }
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
}
