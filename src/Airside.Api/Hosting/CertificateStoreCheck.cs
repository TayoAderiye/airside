using Airside.Core.Containers;
using Airside.Core.Domains;
using Airside.Data;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Hosting;

/// <summary>
/// Warns before the proxy re-issues every certificate at once.
/// </summary>
/// <remarks>
/// <para>
/// Caddy keeps its certificates and its ACME account key in <c>/data</c>. If that
/// volume is lost — a fresh host, a migration where the volume was not carried
/// over, a stack brought up with a renamed volume — Caddy comes back believing it
/// has never issued anything and asks for every certificate again, simultaneously.
/// </para>
/// <para>
/// On a host with more than a handful of domains that trips Let's Encrypt's
/// weekly limit part-way through, so some names get certificates and the rest are
/// locked out for a week. The failure looks like a broken install rather than a
/// missing volume, and the recovery — restoring <c>/data</c> — is not something
/// anyone guesses.
/// </para>
/// <para>
/// So it is detected and named at startup, once, before the damage rather than
/// after.
/// </para>
/// </remarks>
public sealed class CertificateStoreCheck(
    IServiceScopeFactory scopeFactory,
    IContainerRuntime runtime,
    ILogger<CertificateStoreCheck> logger) : IHostedService
{
    /// <summary>The volume Caddy writes its certificates and ACME account key to.</summary>
    public const string CaddyDataVolume = "airside-caddy-data";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

            var expecting = await db.Domains
                .AsNoTracking()
                .CountAsync(d => d.TlsMode == TlsMode.Automatic && d.DetachedAt == null, cancellationToken)
                .ConfigureAwait(false);

            if (expecting == 0)
            {
                return;
            }

            var volume = await runtime.Volumes.FindAsync(CaddyDataVolume, cancellationToken).ConfigureAwait(false);

            if (volume is null)
            {
                logger.LogError(
                    "The {Volume} volume is missing while {Count} domains expect automatic certificates. "
                    + "Caddy will request every one of them again, which can exhaust the weekly issuance "
                    + "limit and leave some hostnames without a certificate for a week. If this host was "
                    + "migrated, restore the volume before the proxy starts.",
                    CaddyDataVolume, expecting);

                return;
            }

            var size = await runtime.Volumes.MeasureAsync(CaddyDataVolume, cancellationToken).ConfigureAwait(false);

            // An existing but empty volume is the same situation and the more
            // confusing one, because the volume is right there in `docker volume
            // ls` and looks fine.
            if (size == 0)
            {
                logger.LogWarning(
                    "The {Volume} volume is empty while {Count} domains expect automatic certificates. "
                    + "Every certificate will be requested again. Include this volume in backups alongside "
                    + "the database and the Data Protection key ring — restoring it is what avoids mass "
                    + "re-issuance after a move.",
                    CaddyDataVolume, expecting);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A diagnostic must never stop the application starting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogDebug(ex, "The certificate store check could not run");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
