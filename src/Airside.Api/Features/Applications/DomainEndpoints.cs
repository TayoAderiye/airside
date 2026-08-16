using Airside.Core.Domains;
using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Jobs;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Applications;

internal static class DomainEndpoints
{
    public static IEndpointRouteBuilder MapDomainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/applications/{id:guid}/domains", ListAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainRead);

        app.MapPost("/api/v1/applications/{id:guid}/domains", AddAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage);

        app.MapPost("/api/v1/domains/{domainId:guid}/delete", RemoveAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage);

        app.MapGet("/api/v1/domains/{domainId:guid}/certificate", GetCertificateAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainRead);

        return app;
    }

    private static async Task<Ok<IReadOnlyList<DomainDto>>> ListAsync(
        Guid id,
        AirsideDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var domains = await db.Domains
            .AsNoTracking()
            .Where(d => d.ApplicationId == id)
            .OrderByDescending(d => d.IsPrimary)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<DomainDto>>(
            [.. domains.Select(d => DomainDto.From(d, timeProvider.GetUtcNow().UtcDateTime))]);
    }

    /// <summary>
    /// Binds a hostname to an application.
    /// </summary>
    /// <remarks>
    /// The response carries the DNS the operator has to set up. Caddy cannot
    /// obtain a certificate until the name resolves here, and the commonest
    /// failure by a wide margin is a domain added before its A record — which
    /// produces an opaque ACME error rather than anything that names the cause.
    /// </remarks>
    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> AddAsync(
        Guid id,
        AddDomainRequest request,
        AirsideDbContext db,
        IJobQueue jobs,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hostname = request.Hostname?.Trim().ToLowerInvariant();
        var validation = ValidateHostname(hostname);

        if (validation.IsFailure)
        {
            return validation.Failure!.ToProblem();
        }

        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);

        if (app is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such application.").ToProblem();
        }

        if (await db.Domains.AnyAsync(d => d.Hostname == hostname, ct).ConfigureAwait(false))
        {
            return new Error(
                ErrorCodes.DomainAlreadyBound,
                $"'{hostname}' is already routed. A hostname can serve one application, or which one "
                + "receives a request would depend on the order the routes happen to be in.").ToProblem();
        }

        var userId = CurrentUserId(http);

        var domain = new Domain
        {
            ApplicationId = id,
            Hostname = hostname!,
            IsPrimary = !await db.Domains.AnyAsync(d => d.ApplicationId == id, ct).ConfigureAwait(false),
            Status = DomainStatus.Pending,
        };

        db.Domains.Add(domain);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            DomainJobTypes.Bind,
            new DomainPayload(domain.Id, Bind: true, domain.Hostname),
            id,
            userId,
            $"{DomainJobTypes.Bind}:{domain.Id}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "domain.bound",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "domain",
            ResourceId = domain.Id,
            ResourceSlugSnapshot = hostname,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, DomainJobTypes.Bind, id);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>
    /// Rejects anything that is not a plausible DNS name.
    /// </summary>
    /// <remarks>
    /// The hostname becomes a Caddy route matcher and part of a route id, so it
    /// is validated rather than escaped. A wildcard is refused explicitly: Caddy
    /// can serve one, but it needs a DNS-01 challenge and provider credentials
    /// Airside does not have, so accepting it would produce a route that never
    /// gets a certificate.
    /// </remarks>
    private static Result ValidateHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return Invalid("A hostname is required.");
        }

        if (hostname.Length > 253)
        {
            return Invalid("A hostname cannot exceed 253 characters.");
        }

        if (hostname.StartsWith('*'))
        {
            return Invalid(
                "Wildcard domains need a DNS-01 challenge and DNS provider credentials, which Airside does "
                + "not manage. Add each hostname individually.");
        }

        if (!HostnamePattern.IsMatch(hostname))
        {
            return Invalid(
                "That is not a valid hostname. Use labels of letters, digits, and hyphens separated by dots, "
                + "for example app.example.com.");
        }

        return Result.Ok();
    }

    private static readonly System.Text.RegularExpressions.Regex HostnamePattern = new(
        @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static Error Invalid(string message) => new(
        ErrorCodes.ValidationFailed,
        message,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "hostname" });

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> RemoveAsync(
        Guid domainId,
        AirsideDbContext db,
        IJobQueue jobs,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such domain.").ToProblem();
        }

        var userId = CurrentUserId(http);

        domain.DeletedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            DomainJobTypes.Unbind,
            new DomainPayload(domainId, Bind: false, domain.Hostname),
            domain.ApplicationId,
            userId,
            $"{DomainJobTypes.Unbind}:{domainId}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "domain.unbound",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "domain",
            ResourceId = domainId,
            ResourceSlugSnapshot = domain.Hostname,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, DomainJobTypes.Unbind, domain.ApplicationId);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>
    /// Reads the certificate live rather than from the cache.
    /// </summary>
    /// <remarks>
    /// The stored fields exist so a list view does not probe every domain. When
    /// somebody asks about one specifically, the honest answer is what is being
    /// served right now.
    /// </remarks>
    private static async Task<Results<Ok<CertificateDto>, ProblemHttpResult>> GetCertificateAsync(
        Guid domainId,
        AirsideDbContext db,
        Core.Proxy.IProxyManager proxy,
        DomainStore store,
        CancellationToken ct)
    {
        var domain = await db.Domains.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such domain.").ToProblem();
        }

        var certificate = await proxy.GetCertificateAsync(domain.Hostname, ct).ConfigureAwait(false);
        await store.RecordCertificateAsync(domainId, certificate, ct).ConfigureAwait(false);

        return TypedResults.Ok(certificate is null
            ? new CertificateDto(
                domain.Hostname, null, null, null, false, false,
                "No certificate is being served yet. The usual cause is DNS: the hostname has to resolve "
                + "to this host before Let's Encrypt can complete its challenge.")
            : new CertificateDto(
                domain.Hostname,
                certificate.Issuer,
                certificate.NotBefore,
                certificate.NotAfter,
                certificate.AutoRenew,
                IsValid: true,
                Detail: null));
    }

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
