using Airside.Core.Domains;
using Airside.Core.Naming;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Applications;

internal sealed class DomainStore(AirsideDbContext db, TimeProvider timeProvider) : IDomainStore
{
    public async Task<DomainTarget?> GetAsync(Guid domainId, CancellationToken ct)
    {
        var row = await db.Domains
            .AsNoTracking()
            .Where(d => d.Id == domainId)
            .Join(db.Applications, d => d.ApplicationId, a => a.Id, (d, a) => new { Domain = d, App = a })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? null : ToTarget(row.Domain, row.App);
    }

    public async Task<IReadOnlyList<DomainTarget>> ListLiveAsync(CancellationToken ct)
    {
        var rows = await db.Domains
            .AsNoTracking()
            .Join(db.Applications, d => d.ApplicationId, a => a.Id, (d, a) => new { Domain = d, App = a })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => ToTarget(r.Domain, r.App)).OfType<DomainTarget>()];
    }

    private static DomainTarget? ToTarget(Domain domain, Application app)
    {
        if (!Airside.Core.Common.Slug.TryCreate(app.Slug, out var slug))
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
            app.ContainerPort);
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

        // Pending, not Active. The route exists, but a certificate only arrives
        // once DNS points here and Caddy completes the ACME challenge — claiming
        // Active before that would tell the operator the domain is finished when
        // it may never issue.
        domain.Status = DomainStatus.Pending;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordFailedAsync(Guid domainId, string code, CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return;
        }

        domain.Status = DomainStatus.Failed;
        domain.ErrorCode = code;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordCertificateAsync(
        Guid domainId,
        Airside.Core.Proxy.CertificateStatus? certificate,
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
            // Left Pending rather than moved to Failed. A domain whose DNS has
            // not propagated is not broken, and marking it failed would have
            // operators chasing a problem that resolves itself.
            return;
        }

        domain.CertificateIssuer = certificate.Issuer;
        domain.CertificateNotBefore = certificate.NotBefore.UtcDateTime;
        domain.CertificateNotAfter = certificate.NotAfter.UtcDateTime;
        domain.CertificateAutoRenew = certificate.AutoRenew;
        domain.Status = DomainStatus.Active;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
