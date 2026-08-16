using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Features.Applications;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Domains;
using Airside.Core.Jobs;
using Airside.Core.Proxy;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Dns;
using Airside.Runtime.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Domains;

internal static class DomainMoveEndpoints
{
    public static IEndpointRouteBuilder MapDomainMoveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/domains/{domainId:guid}/move", MoveAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage);

        app.MapPost("/api/v1/applications/{id:guid}/domains/apex-and-www", AddPairAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        return app;
    }

    /// <summary>
    /// Moves a hostname to another application without a gap in service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Detaching and re-attaching would work, but it withdraws the route, waits
    /// for a job, and registers a new one — a window, however short, in which the
    /// hostname serves nothing. It also gives up the certificate: a detach clears
    /// the route and a fresh attach starts issuance again, spending a
    /// duplicate-certificate slot for a change that never needed a new certificate
    /// at all.
    /// </para>
    /// <para>
    /// Instead the upstream is swapped in place. Caddy's PATCH replaces the route
    /// object atomically, so at no point does the hostname have no route — the
    /// same mechanism a deployment cutover uses.
    /// </para>
    /// </remarks>
    private static async Task<Results<Ok<DomainDto>, ProblemHttpResult>> MoveAsync(
        Guid domainId,
        MoveDomainRequest request,
        AirsideDbContext db,
        IProxyManager proxy,
        DomainStore store,
        IContainerRuntimeAccessor runtime,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such domain.").ToProblem();
        }

        if (!string.Equals(request.ConfirmHostname?.Trim().ToLowerInvariant(), domain.Hostname, StringComparison.Ordinal))
        {
            return new Error(
                ErrorCodes.WorkloadConfirmationMismatch,
                $"Type '{domain.Hostname}' to confirm. Moving it changes which application the public sees "
                + "at that address, immediately.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["confirmField"] = "confirmHostname",
                    ["expected"] = domain.Hostname,
                }).ToProblem();
        }

        if (domain.ApplicationId == request.TargetApplicationId)
        {
            return TypedResults.Ok(DomainDto.From(domain, timeProvider.GetUtcNow().UtcDateTime));
        }

        var target = await db.Applications
            .FirstOrDefaultAsync(a => a.Id == request.TargetApplicationId, ct).ConfigureAwait(false);

        if (target is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such target application.").ToProblem();
        }

        if (target.ContainerId is null)
        {
            // Moving onto an application that has never run would swap the route
            // to an upstream that resolves to nothing — a working site replaced by
            // a gateway error.
            return new Error(
                "domain.application_not_deployed",
                $"'{target.Slug}' has never been deployed, so moving '{domain.Hostname}' to it would take "
                + "the hostname offline. Deploy it first.").ToProblem();
        }

        var previousApplicationId = domain.ApplicationId;
        domain.ApplicationId = request.TargetApplicationId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var moved = await store.GetAsync(domainId, ct).ConfigureAwait(false);

        if (moved?.CurrentContainerName is null)
        {
            domain.ApplicationId = previousApplicationId;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new Error(
                "domain.application_not_deployed",
                "The target application has no running container to route to.").ToProblem();
        }

        // The proxy has to reach the new application's network before the route
        // names a container on it, or the swap points at something unresolvable.
        await runtime.AttachProxyAsync(moved.ApplicationNetworkName, ct).ConfigureAwait(false);

        await proxy.UpsertRouteAsync(
            new RouteSpec(
                moved.Hostname,
                new UpstreamTarget(moved.CurrentContainerName, moved.ContainerPort),
                moved.TlsMode,
                moved.Hsts,
                moved.RedirectTo,
                Maintenance: !moved.ApplicationIsRunning),
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "domain.moved",
            Result = AuditResult.Success,
            UserId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null,
            ResourceKind = "domain",
            ResourceId = domainId,
            ResourceSlugSnapshot = domain.Hostname,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["from"] = previousApplicationId,
                ["to"] = request.TargetApplicationId,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(DomainDto.From(domain, timeProvider.GetUtcNow().UtcDateTime));
    }

    /// <summary>
    /// Adds an apex and its www together, with one redirecting to the other.
    /// </summary>
    /// <remarks>
    /// Both hostnames are almost always wanted, and wiring the redirect by hand
    /// means adding two domains and then knowing that the second one needs a
    /// redirect target set — which is easy to forget and produces a www that
    /// serves a duplicate site rather than pointing at the real one.
    /// </remarks>
    private static async Task<Results<Ok<IReadOnlyList<DomainDto>>, ProblemHttpResult>> AddPairAsync(
        Guid id,
        AddApexAndWwwRequest request,
        AirsideDbContext db,
        IDomainPreflight preflight,
        IPublicSuffixList suffixes,
        IJobQueue jobs,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<TlsMode>(request.TlsMode, ignoreCase: true, out var mode))
        {
            return new Error(ErrorCodes.ValidationFailed, "A TLS mode is required.").ToProblem();
        }

        if (!PublicSuffixList.TryNormalise(request.Hostname, out var input, out _))
        {
            return new Error(ErrorCodes.ValidationFailed, "That is not a valid hostname.").ToProblem();
        }

        // Accepts either form and works out the pair, so "www.example.com" and
        // "example.com" both produce the same two domains.
        var apex = input.StartsWith("www.", StringComparison.Ordinal) ? input[4..] : input;
        var www = "www." + apex;

        var registered = suffixes.GetRegisteredDomain(apex);

        if (registered is null || !string.Equals(registered, apex, StringComparison.Ordinal))
        {
            return new Error(
                "domain.not_an_apex",
                $"'{apex}' is not the apex of a registered domain, so there is no www pair to create. "
                + "Add the hostname on its own instead.").ToProblem();
        }

        foreach (var hostname in new[] { apex, www })
        {
            if (await db.Domains.AnyAsync(d => d.Hostname == hostname, ct).ConfigureAwait(false))
            {
                return new Error(
                    ErrorCodes.DomainAlreadyBound,
                    $"'{hostname}' is already routed. Remove it first, or add the remaining hostname on "
                    + "its own.").ToProblem();
            }
        }

        var redirectWww = !string.Equals(request.Redirect, "apex_to_www", StringComparison.OrdinalIgnoreCase);
        var serving = redirectWww ? apex : www;
        var redirecting = redirectWww ? www : apex;

        // Only the serving hostname is pre-flighted. The redirecting one needs no
        // certificate of its own unless it is served over HTTPS — and it is, so
        // both are checked, but a failure on the redirect side is reported without
        // blocking the pair.
        var report = await preflight.RunAsync(new PreflightRequest(serving, mode), ct).ConfigureAwait(false);

        if (report.Blocks)
        {
            var first = report.Blocking.First();

            return new Error(first.Id, first.Summary, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["found"] = first.Found,
                ["expected"] = first.Expected,
                ["remedy"] = first.Remedy,
            }).ToProblem();
        }

        var userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null;
        var created = new List<Domain>();

        // The serving hostname first, so the redirect has something to point at.
        foreach (var hostname in new[] { serving, redirecting })
        {
            var domain = new Domain
            {
                Id = Guid.CreateVersion7(),
                ApplicationId = id,
                Hostname = hostname,
                DisplayHostname = hostname,
                RegisteredDomain = registered,
                TlsMode = mode,
                IsPrimary = hostname == serving && created.Count == 0,
                Status = DomainStatus.Pending,
                RedirectToDomainId = hostname == redirecting ? created[0].Id : null,
                CertificateAutoRenew = mode is TlsMode.Automatic or TlsMode.Internal,
            };

            db.Domains.Add(domain);
            created.Add(domain);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var domain in created)
        {
            await jobs.EnqueueAsync(
                DomainJobTypes.Bind,
                new DomainPayload(domain.Id, Bind: true, domain.Hostname),
                id,
                userId,
                $"{DomainJobTypes.Bind}:{domain.Id}",
                ct).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        return TypedResults.Ok<IReadOnlyList<DomainDto>>([.. created.Select(d => DomainDto.From(d, now))]);
    }
}

/// <summary>Attaches the proxy to an application network, without the endpoint knowing about Docker.</summary>
public interface IContainerRuntimeAccessor
{
    Task AttachProxyAsync(string networkName, CancellationToken ct);
}
