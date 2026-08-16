using Airside.Core.Naming;
using Airside.Core.Proxy;
using Airside.Data;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Hosting;

/// <summary>
/// Consumes the marker left by <c>airside domain reset</c>.
/// </summary>
/// <remarks>
/// <para>
/// The other half of the only unrecoverable mistake Airside can make. Setting the
/// dashboard to a hostname that does not resolve here leaves no way in: the API
/// is not published to the host, so there is no address to fall back to and no
/// interface left to correct it from.
/// </para>
/// <para>
/// The CLI writes a file because it has to work when nothing else does. This
/// reads it at startup, clears the domain, withdraws the route, and deletes the
/// marker so a single reset does not repeat on every restart.
/// </para>
/// </remarks>
public sealed class DomainResetCheck(
    IServiceScopeFactory scopeFactory,
    IProxyManager proxy,
    ILogger<DomainResetCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AirsideLabels.HostPaths.DomainReset))
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

            var settings = await db.InstanceSettings.FirstAsync(cancellationToken).ConfigureAwait(false);
            var previous = settings.DashboardDomain;

            settings.DashboardDomain = null;
            settings.PreviousDashboardDomain = null;
            settings.PreviousDashboardDomainUntil = null;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (previous is not null)
            {
                // Best effort. The point is to restore access, and a proxy that is
                // not answering must not stop the domain being cleared.
                try
                {
                    await proxy.RemoveRouteAsync(previous, cancellationToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    logger.LogWarning(ex, "Could not withdraw the route for {Hostname} during the reset", previous);
                }
            }

            File.Delete(AirsideLabels.HostPaths.DomainReset);

            logger.LogWarning(
                "The dashboard domain was reset from {Previous} by the CLI. Airside is reachable on this "
                + "server's address again.",
                previous ?? "(none)");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The marker stays if this fails, so the next start retries.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "The dashboard domain reset could not be applied; it will be retried on the next start");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
