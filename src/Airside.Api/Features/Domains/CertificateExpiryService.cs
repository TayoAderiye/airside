using Airside.Core.Domains;
using Airside.Core.Proxy;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Domains;

/// <summary>
/// Watches certificate expiry and warns before a site goes dark.
/// </summary>
/// <remarks>
/// <para>
/// This exists for <see cref="TlsMode.Manual"/>, where nothing renews anything.
/// The failure mode is specific and awful: ninety days after a successful setup —
/// long after everyone has moved on — the certificate expires at whatever hour it
/// happens to expire, and every browser refuses the site. The only warning anyone
/// receives is the one Airside chooses to send.
/// </para>
/// <para>
/// It also refreshes the cache for automatic domains. Caddy renews those itself,
/// so a short expiry there is not a task but a symptom — renewal has been failing
/// silently, and it wants a different message.
/// </para>
/// </remarks>
public sealed class CertificateExpiryService(
    IServiceScopeFactory scopeFactory,
    IProxyManager proxy,
    TimeProvider timeProvider,
    ILogger<CertificateExpiryService> logger) : BackgroundService
{
    /// <summary>
    /// The thresholds a notification fires on.
    /// </summary>
    /// <remarks>
    /// Several, and escalating, because one warning at thirty days is a warning
    /// nobody acts on and nobody sees again. The day-of entry is deliberate: by
    /// then it is an emergency and should read like one.
    /// </remarks>
    private static readonly int[] Thresholds = [30, 14, 7, 3, 1, 0];

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);

        do
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

            var now = timeProvider.GetUtcNow();

            var domains = await db.Domains
                .Where(d => d.DetachedAt == null && d.TlsMode != TlsMode.External)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var domain in domains)
            {
                await RefreshAsync(domain, now, ct).ConfigureAwait(false);
                Evaluate(domain, now);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A sweep must never take the process down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "The certificate expiry sweep failed; it will run again on the next tick");
        }
    }

    /// <summary>
    /// Reads what is actually being served, rather than trusting the stored dates.
    /// </summary>
    /// <remarks>
    /// A renewal that Caddy completed leaves the cached <c>NotAfter</c> stale, and
    /// warning about a certificate that was replaced days ago is exactly the kind
    /// of false alarm that teaches people to ignore the alerts.
    /// </remarks>
    private async Task RefreshAsync(Domain domain, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var served = await proxy.GetCertificateAsync(domain.Hostname, ct).ConfigureAwait(false);

            if (served is null)
            {
                return;
            }

            domain.CertificateIssuer = served.Issuer;
            domain.CertificateNotBefore = served.NotBefore.UtcDateTime;
            domain.CertificateNotAfter = served.NotAfter.UtcDateTime;
            domain.LastCertificateCheckAt = now.UtcDateTime;
        }
#pragma warning disable CA1031 // One unreachable hostname must not stop the sweep.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogDebug(ex, "Could not read the served certificate for {Hostname}", domain.Hostname);
        }
    }

    /// <summary>
    /// Moves the domain's status and logs at each threshold it has crossed.
    /// </summary>
    /// <remarks>
    /// <see cref="DomainStatus.Expiring"/> is a distinct status rather than a
    /// warning flag so the list view can sort and colour by it. A countdown buried
    /// in a detail panel is a countdown nobody reads.
    /// </remarks>
    private void Evaluate(Domain domain, DateTimeOffset now)
    {
        if (domain.CertificateNotAfter is not { } notAfter)
        {
            return;
        }

        var days = (int)Math.Floor((notAfter - now.UtcDateTime).TotalDays);

        if (days < 0)
        {
            domain.Status = DomainStatus.Expired;

            logger.LogError(
                "The certificate for {Hostname} expired {Days} days ago and the site is being refused by "
                + "browsers. {Action}",
                domain.Hostname, Math.Abs(days), Action(domain));

            return;
        }

        var crossed = Array.Find(Thresholds, t => days <= t);

        if (crossed is 0 && days > 0)
        {
            return;
        }

        if (days <= 30)
        {
            domain.Status = DomainStatus.Expiring;

            // Automatic renewal happens at thirty days remaining. Anything under
            // that on an auto-renewing domain means renewal has been failing, so
            // it is a fault rather than a task and is logged as one.
            if (domain.CertificateAutoRenew)
            {
                logger.LogWarning(
                    "The certificate for {Hostname} expires in {Days} days and automatic renewal has not "
                    + "run. Check that the hostname still resolves here and that port 80 is reachable.",
                    domain.Hostname, days);
            }
            else
            {
                logger.LogWarning(
                    "The certificate for {Hostname} expires in {Days} days. {Action}",
                    domain.Hostname, days, Action(domain));
            }
        }
    }

    private static string Action(Domain domain) => domain.CertificateAutoRenew
        ? "Airside will attempt to renew it automatically."
        : $"Nothing renews it automatically — upload a replacement for '{domain.Hostname}'.";
}
