using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Jobs;
using Airside.Core.Security;
using Airside.Core.Workloads;
using Airside.Data;
using Airside.Runtime.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Applications;

/// <param name="ConfirmSlug">
/// The application's slug, typed back. Deleting is not undoable and the name is
/// the only thing that distinguishes one application from another in a hurry.
/// </param>
/// <param name="ReleaseDomains">
/// Required when domains are attached. Without an explicit answer the request is
/// refused rather than guessed at — see the endpoint for why.
/// </param>
public sealed record DeleteApplicationRequest(
    string ConfirmSlug,
    bool DeleteVolumes = false,
    bool? ReleaseDomains = null);

internal static class ApplicationLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapApplicationLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/applications").WithTags("Applications");

        group.MapPost("/{id:guid}/start", (Guid id, HttpContext http, LifecycleServices s, CancellationToken ct) =>
                s.RunAsync(id, ApplicationJobTypes.Start, http, ct))
            .RequirePermission(Permissions.ApplicationLifecycle);

        group.MapPost("/{id:guid}/stop", (Guid id, HttpContext http, LifecycleServices s, CancellationToken ct) =>
                s.RunAsync(id, ApplicationJobTypes.Stop, http, ct))
            .RequirePermission(Permissions.ApplicationLifecycle);

        group.MapPost("/{id:guid}/restart", (Guid id, HttpContext http, LifecycleServices s, CancellationToken ct) =>
                s.RunAsync(id, ApplicationJobTypes.Restart, http, ct))
            .RequirePermission(Permissions.ApplicationLifecycle);

        group.MapPost("/{id:guid}/delete", DeleteAsync)
            .RequirePermission(Permissions.ApplicationDelete)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        return app;
    }

    /// <summary>
    /// Deletes an application, once it is clear what should happen to its domains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deletion that silently left the routes behind would point live hostnames
    /// at a container that no longer exists, so real visitors would get a gateway
    /// error from a site the operator believes is gone. Cascading the domains away
    /// without asking is the opposite mistake: those hostnames have DNS pointed at
    /// this server and possibly certificates issued against a weekly limit.
    /// </para>
    /// <para>
    /// So when domains are attached the request is refused until the caller says
    /// which they meant. It is one extra round trip, on an operation nobody
    /// performs twice a day, in exchange for never guessing.
    /// </para>
    /// </remarks>
    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> DeleteAsync(
        Guid id,
        DeleteApplicationRequest request,
        AirsideDbContext db,
        IJobQueue jobs,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);

        if (app is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such application.").ToProblem();
        }

        if (!string.Equals(request.ConfirmSlug, app.Slug, StringComparison.Ordinal))
        {
            return new Error(
                ErrorCodes.WorkloadConfirmationMismatch,
                $"Type '{app.Slug}' to confirm. Deleting an application removes its container and network, "
                + "and cannot be undone.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["confirmField"] = "confirmSlug",
                    ["expected"] = app.Slug,
                }).ToProblem();
        }

        var hostnames = await db.Domains
            .AsNoTracking()
            .Where(d => d.ApplicationId == id)
            .Select(d => d.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (hostnames.Count > 0 && request.ReleaseDomains is not true)
        {
            return new Error(
                "application.domains_attached",
                $"'{app.Slug}' still serves {hostnames.Count} domain(s): {string.Join(", ", hostnames)}. "
                + "Set releaseDomains to true to withdraw them as part of the delete, or detach them first "
                + "if you want to move them to another application.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["hostnames"] = hostnames,
                    ["confirmField"] = "releaseDomains",
                }).ToProblem();
        }

        var userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null;

        app.State = nameof(ApplicationState.Deleting);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            ApplicationJobTypes.Delete,
            new ApplicationDeletePayload(id, request.DeleteVolumes, ReleaseDomains: hostnames.Count > 0),
            id,
            userId,
            $"{ApplicationJobTypes.Delete}:{id}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "application.deleted",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "application",
            ResourceId = id,
            ResourceSlugSnapshot = app.Slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deleteVolumes"] = request.DeleteVolumes,
                ["releasedDomains"] = hostnames,
            },
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, ApplicationJobTypes.Delete, id);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }
}

/// <summary>The shared start/stop/restart path, so the three endpoints stay one behaviour.</summary>
internal sealed class LifecycleServices(
    AirsideDbContext db,
    IJobQueue jobs,
    IAuditWriter audit)
{
    public async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> RunAsync(
        Guid id,
        string jobType,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var app = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);

        if (app is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such application.").ToProblem();
        }

        if (app.ContainerId is null)
        {
            return new Error(
                "application.not_deployed",
                $"'{app.Slug}' has never been deployed, so there is nothing to start or stop.").ToProblem();
        }

        var userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null;

        var jobId = await jobs.EnqueueAsync(
            jobType,
            new ApplicationLifecyclePayload(id),
            id,
            userId,

            // Keyed on the job type as well as the application, so a stop and a
            // start queued together are two jobs rather than one deduplicated into
            // whichever arrived first.
            $"{jobType}:{id}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = jobType,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "application",
            ResourceId = id,
            ResourceSlugSnapshot = app.Slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, jobType, id);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }
}
