using Airside.Core.Common;
using Airside.Core.Domains;
using Airside.Core.Naming;
using Airside.Core.Proxy;
using Airside.Core.Security;
using Airside.Core.Workloads;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Domains;
using Airside.Runtime.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Applications;

internal sealed class DomainStore(
    AirsideDbContext db,
    ISecretProtector protector,
    TimeProvider timeProvider) : IDomainStore, IHostnameRegistry
{
    public async Task<DomainTarget?> GetAsync(Guid domainId, CancellationToken ct)
    {
        var row = await db.Domains
            .AsNoTracking()
            .Where(d => d.Id == domainId)
            .Join(db.Applications, d => d.ApplicationId, a => a.Id, (d, a) => new { Domain = d, App = a })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var redirectTo = row.Domain.RedirectToDomainId is { } id
            ? await db.Domains.AsNoTracking()
                .Where(d => d.Id == id)
                .Select(d => d.Hostname)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
            : null;

        return ToTarget(row.Domain, row.App, redirectTo);
    }

    public async Task<IReadOnlyList<DomainTarget>> ListLiveAsync(CancellationToken ct)
    {
        var rows = await db.Domains
            .AsNoTracking()
            .Where(d => d.DetachedAt == null)
            .Join(db.Applications, d => d.ApplicationId, a => a.Id, (d, a) => new { Domain = d, App = a })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Resolved in one pass rather than per row: a redirect target is just
        // another domain, and N+1 queries here would run on every reconciliation
        // pass for the lifetime of the process.
        var hostnames = await db.Domains.AsNoTracking()
            .Select(d => new { d.Id, d.Hostname })
            .ToDictionaryAsync(d => d.Id, d => d.Hostname, ct)
            .ConfigureAwait(false);

        return
        [
            .. rows
                .Select(r => ToTarget(
                    r.Domain,
                    r.App,
                    r.Domain.RedirectToDomainId is { } id && hostnames.TryGetValue(id, out var host) ? host : null))
                .OfType<DomainTarget>(),
        ];
    }

    /// <summary>
    /// Groups the non-Automatic hostnames by what Caddy has to be told about each.
    /// </summary>
    /// <remarks>
    /// The grouping is not cosmetic. External needs <c>skip</c>, which switches
    /// HTTPS off for the host entirely; Manual needs <c>skip_certificates</c>,
    /// which keeps TLS on and only stops Caddy fetching a certificate. Putting a
    /// Manual hostname in the first list loads the uploaded certificate perfectly
    /// and then serves nothing on 443.
    /// </remarks>
    public async Task<TlsPolicySet> GetTlsPolicyAsync(CancellationToken ct)
    {
        var rows = await db.Domains
            .AsNoTracking()
            .Where(d => d.TlsMode != TlsMode.Automatic && d.DetachedAt == null)
            .Select(d => new { d.Hostname, d.TlsMode })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new TlsPolicySet(
            [.. rows.Where(r => r.TlsMode == TlsMode.External).Select(r => r.Hostname)],
            [.. rows.Where(r => r.TlsMode == TlsMode.Manual).Select(r => r.Hostname)],
            [.. rows.Where(r => r.TlsMode == TlsMode.Internal).Select(r => r.Hostname)]);
    }

    private static DomainTarget? ToTarget(Domain domain, Application app, string? redirectTo)
    {
        if (!Slug.TryCreate(app.Slug, out var slug))
        {
            return null;
        }

        // The upstream is the container's *name*, not its address. A container
        // replaced by a deployment gets a new IP, and a route pinned to an
        // address would keep pointing at the one that has gone.
        var containerName = app.CurrentDeploymentId is null
            ? null
            : AirsideNames.ApplicationContainer(slug, app.CurrentDeploymentId.Value);

        return new DomainTarget(
            domain.Id,
            domain.Hostname,
            app.Id,
            app.Slug,
            AirsideNames.ApplicationNetwork(slug),
            app.ContainerId is null ? null : containerName,
            app.ContainerPort,
            domain.TlsMode,
            domain.CertificateSecretId,
            domain.HstsEnabled
                ? new HstsPolicy(domain.HstsMaxAgeSeconds, domain.HstsIncludeSubdomains, domain.HstsPreload)
                : null,
            redirectTo,
            string.Equals(app.State, nameof(ApplicationState.Running), StringComparison.Ordinal));
    }

    public async Task RecordStatusAsync(Guid domainId, DomainStatus status, CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return;
        }

        domain.Status = status;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordBoundAsync(Guid domainId, string routeId, CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return;
        }

        domain.RouteId = routeId;
        domain.ErrorCode = null;
        domain.ErrorMessage = null;

        // Not Active. The route exists, but for Automatic a certificate only
        // arrives once Caddy completes the challenge — claiming Active before that
        // would tell the operator the domain is finished when it may never issue.
        // The certificate poll is what promotes it.
        domain.Status = domain.TlsMode switch
        {
            TlsMode.Automatic => DomainStatus.Issuing,

            // Nothing further has to happen for these: the certificate is already
            // loaded, or TLS is somebody else's job entirely.
            TlsMode.Manual or TlsMode.External or TlsMode.Internal => DomainStatus.Active,
            _ => DomainStatus.Pending,
        };

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordFailedAsync(Guid domainId, string code, string? message, CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return;
        }

        domain.Status = DomainStatus.Failed;
        domain.ErrorCode = code;
        domain.ErrorMessage = message;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the stored key back out for handing to Caddy.
    /// </summary>
    /// <remarks>
    /// The only place a private key is decrypted. A key ring restored from another
    /// instance produces a failure here rather than an exception, because that is
    /// a recoverable operator mistake and deserves a message naming it.
    /// </remarks>
    public async Task<ManualCertificate?> GetManualCertificateAsync(Guid domainId, CancellationToken ct)
    {
        var domain = await db.Domains.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return null;
        }

        var certificate = await db.DomainCertificates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.DomainId == domainId, ct).ConfigureAwait(false);

        if (certificate is null)
        {
            return null;
        }

        var key = protector.Unprotect(certificate.EncryptedPrivateKey);

        if (key.IsFailure)
        {
            throw new InvalidOperationException(
                $"The private key for '{domain.Hostname}' could not be decrypted. This usually means the "
                + "Data Protection key ring was replaced or restored from a different instance. Upload "
                + "the certificate again.");
        }

        return new ManualCertificate(domain.Hostname, certificate.ChainPem, key.Value);
    }

    /// <inheritdoc />
    public async Task<string?> WhoHoldsAsync(string hostname, Guid? exclude, CancellationToken ct) =>
        await db.Domains
            .AsNoTracking()
            .Where(d => d.Hostname == hostname && (exclude == null || d.Id != exclude))
            .Join(db.Applications, d => d.ApplicationId, a => a.Id, (d, a) => a.Slug)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string?> GetDashboardHostnameAsync(CancellationToken ct) =>
        await db.InstanceSettings
            .AsNoTracking()
            .Select(s => s.DashboardDomain)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task RecordCertificateAsync(
        Guid domainId,
        CertificateStatus? certificate,
        CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return;
        }

        domain.LastCertificateCheckAt = timeProvider.GetUtcNow().UtcDateTime;

        if (certificate is null)
        {
            // Left as-is rather than moved to Failed. A domain whose DNS has not
            // propagated is not broken, and marking it failed would have operators
            // chasing a problem that resolves itself.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        domain.CertificateIssuer = certificate.Issuer;
        domain.CertificateNotBefore = certificate.NotBefore.UtcDateTime;
        domain.CertificateNotAfter = certificate.NotAfter.UtcDateTime;
        domain.CertificateAutoRenew = certificate.AutoRenew;

        // Staging certificates are issued by "(STAGING)"-prefixed authorities and
        // are trusted by nothing. Reporting one as Active would put a healthy
        // badge on a hostname every browser refuses.
        domain.CertificateIsStaging = certificate.Issuer.Contains("STAGING", StringComparison.OrdinalIgnoreCase)
            || certificate.Issuer.Contains("Fake LE", StringComparison.OrdinalIgnoreCase);

        domain.Status = domain.CertificateIsStaging ? DomainStatus.Pending : DomainStatus.Active;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
