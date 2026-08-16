using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Databases;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Applications;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Applications;

internal static class ApplicationEndpoints
{
    public static IEndpointRouteBuilder MapApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/applications").WithTags("Applications").RequireAuthorization();

        group.MapGet("/", ListAsync).RequirePermission(Permissions.ApplicationRead);
        group.MapGet("/{id:guid}", GetAsync).RequirePermission(Permissions.ApplicationRead);
        group.MapPost("/", CreateAsync).RequirePermission(Permissions.ApplicationCreate);

        group.MapPost("/{id:guid}/deployments", DeployAsync).RequirePermission(Permissions.ApplicationDeploy);
        group.MapGet("/{id:guid}/deployments", ListDeploymentsAsync).RequirePermission(Permissions.ApplicationRead);

        group.MapGet("/{id:guid}/environment", GetEnvironmentAsync).RequirePermission(Permissions.SecretView);
        group.MapPut("/{id:guid}/environment/{key}", SetEnvironmentAsync).RequirePermission(Permissions.SecretWrite);
        group.MapPost("/{id:guid}/environment/{key}/reveal", RevealAsync).RequirePermission(Permissions.SecretRead);
        group.MapDelete("/{id:guid}/environment/{key}", DeleteEnvironmentAsync)
            .RequirePermission(Permissions.SecretWrite);

        group.MapGet("/{id:guid}/databases", ListAttachmentsAsync).RequirePermission(Permissions.ApplicationRead);
        group.MapPost("/{id:guid}/databases", AttachAsync)
            .RequirePermission(Permissions.ApplicationAttachDatabase);
        group.MapDelete("/{id:guid}/databases/{attachmentId:guid}", DetachAsync)
            .RequirePermission(Permissions.ApplicationAttachDatabase);

        app.MapGet("/api/v1/deployments/{id:guid}", GetDeploymentAsync)
            .WithTags("Applications")
            .RequirePermission(Permissions.ApplicationRead);

        app.MapGet("/api/v1/deployments/{id:guid}/log", GetDeploymentLogAsync)
            .WithTags("Applications")
            .RequirePermission(Permissions.ApplicationRead);

        app.MapPost("/api/v1/deployments/{id:guid}/rollback", RollbackAsync)
            .WithTags("Applications")
            .RequirePermission(Permissions.ApplicationRollback)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        return app;
    }

    private static async Task<Ok<PagedResult<ApplicationSummaryDto>>> ListAsync(
        AirsideDbContext db,
        SystemWorkloadReader system,
        CancellationToken ct,
        int page = 1,
        int pageSize = 25)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var total = await db.Applications.CountAsync(ct).ConfigureAwait(false);

        var apps = await db.Applications
            .AsNoTracking()
            .OrderBy(a => a.Slug)
            .Skip((Math.Max(1, page) - 1) * size)
            .Take(size)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Airside's own containers, on the first page only. They are discovered
        // rather than queried, so they cannot take part in the paging arithmetic
        // without either lying about the total or reading Docker once per page.
        var systemApps = Math.Max(1, page) == 1
            ? await system.ApplicationsAsync(ct).ConfigureAwait(false)
            : [];

        return TypedResults.Ok(new PagedResult<ApplicationSummaryDto>(
            [.. systemApps, .. apps.Select(ApplicationSummaryDto.From)],
            Math.Max(1, page),
            size,
            total));
    }

    private static async Task<Results<Ok<ApplicationSummaryDto>, NotFound>> GetAsync(
        Guid id,
        AirsideDbContext db,
        CancellationToken ct)
    {
        var app = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);

        return app is null ? TypedResults.NotFound() : TypedResults.Ok(ApplicationSummaryDto.From(app));
    }

    /// <summary>
    /// Creating an application is synchronous and returns 201.
    /// </summary>
    /// <remarks>
    /// It writes a record and starts nothing. Conflating creation with the first
    /// deploy would mean a typo in a Dockerfile path leaves you with an
    /// application you can only fix by deleting it.
    /// </remarks>
    private static async Task<Results<Created<ApplicationSummaryDto>, ProblemHttpResult>> CreateAsync(
        CreateApplicationRequest request,
        ApplicationService service,
        AirsideDbContext db,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        var result = await service.CreateAsync(request, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Failure!.ToProblem();
        }

        var created = await db.Applications.AsNoTracking()
            .FirstAsync(a => a.Id == result.Value, ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "application.created",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "application",
            ResourceId = created.Id,
            ResourceSlugSnapshot = created.Slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.Created(
            $"/api/v1/applications/{created.Id}", ApplicationSummaryDto.From(created));
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> DeployAsync(
        Guid id,
        DeployRequest request,
        ApplicationService service,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        var result = await service.DeployAsync(id, request, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Failure!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.ApplicationDeployed,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "application",
            ResourceId = id,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.Accepted(result.Value.StatusUrl, result.Value);
    }

    private static async Task<Ok<CursorResult<DeploymentDto>>> ListDeploymentsAsync(
        Guid id,
        AirsideDbContext db,
        CancellationToken ct,
        int limit = 50)
    {
        var deployments = await db.Deployments
            .AsNoTracking()
            .Where(d => d.ApplicationId == id)
            .OrderByDescending(d => d.Number)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rollbackable = deployments.Exists(d =>
            d.Status == DeploymentStatus.Succeeded && !string.IsNullOrEmpty(d.ImageDigest) && !d.IsCurrent);

        return TypedResults.Ok(new CursorResult<DeploymentDto>(
            [.. deployments.Select(d => DeploymentDto.From(d, rollbackable))], null));
    }

    private static async Task<Results<Ok<DeploymentDto>, NotFound>> GetDeploymentAsync(
        Guid id,
        AirsideDbContext db,
        CancellationToken ct)
    {
        var deployment = await db.Deployments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);

        return deployment is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(DeploymentDto.From(deployment, !string.IsNullOrEmpty(deployment.ImageDigest)));
    }

    private static async Task<Results<ContentHttpResult, NotFound>> GetDeploymentLogAsync(
        Guid id,
        AirsideDbContext db,
        CancellationToken ct)
    {
        var log = await db.DeploymentLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.DeploymentId == id, ct).ConfigureAwait(false);

        if (log is not null)
        {
            // Plain text, not JSON. A build log is read by a human looking for
            // the line that failed, and wrapping it in a JSON string doubles its
            // size and escapes every newline.
            return TypedResults.Text(log.Content, "text/plain; charset=utf-8");
        }

        // No log row is not the same as no deployment. A build that has not
        // produced output yet has none, and a deployment from a prebuilt image
        // never will — so 404 here made a screen polling for progress emit a
        // failed request every two seconds, forever, against a deployment that
        // was working perfectly.
        //
        // 404 is reserved for the deployment itself being absent, which is the
        // only case a caller can do anything about.
        var exists = await db.Deployments.AsNoTracking()
            .AnyAsync(d => d.Id == id, ct).ConfigureAwait(false);

        return exists
            ? TypedResults.Text(string.Empty, "text/plain; charset=utf-8")
            : TypedResults.NotFound();
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> RollbackAsync(
        Guid id,
        ApplicationService service,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        var result = await service.RollbackAsync(id, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Failure!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.ApplicationRolledBack,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "deployment",
            ResourceId = id,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.Accepted(result.Value.StatusUrl, result.Value);
    }

    /// <summary>
    /// The merged environment: manual entries plus what each attachment injects.
    /// </summary>
    /// <remarks>
    /// Injected entries appear here but are not stored, and are marked
    /// <c>editable: false</c> — editing one would be overwritten at the next
    /// deploy, because they are rendered from the attachment and the live
    /// credential every time.
    /// </remarks>
    private static async Task<Ok<IReadOnlyList<EnvironmentEntryDto>>> GetEnvironmentAsync(
        Guid id,
        AirsideDbContext db,
        EnvironmentRenderer renderer,
        CancellationToken ct)
    {
        var manual = await db.EnvironmentVariables
            .AsNoTracking()
            .Where(v => v.ApplicationId == id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var attachments = await db.DatabaseAttachments
            .AsNoTracking()
            .Where(a => a.ApplicationId == id && a.DetachedAt == null)
            .Join(db.Databases, a => a.DatabaseInstanceId, d => d.Id, (a, d) => new { a.Id, a.EnvKeyPrefix, d.Engine })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var entries = new List<EnvironmentEntryDto>();

        foreach (var attachment in attachments)
        {
            foreach (var key in renderer.InjectedKeysFor(attachment.Engine, attachment.EnvKeyPrefix))
            {
                var sensitive = key.EndsWith("PASSWORD", StringComparison.Ordinal)
                    || key.EndsWith("URL", StringComparison.Ordinal)
                    || key.EndsWith("URI", StringComparison.Ordinal);

                entries.Add(new EnvironmentEntryDto(
                    key,
                    sensitive ? Secret.Mask : "(injected at deploy)",
                    sensitive,
                    "attachment",
                    attachment.Id,
                    Editable: false,
                    RevealUrl: null,
                    UpdatedAt: null));
            }
        }

        entries.AddRange(manual.Select(v => new EnvironmentEntryDto(
            v.Key,
            v.IsSecret ? Secret.Mask : v.Value,
            v.IsSecret,
            "manual",
            null,
            Editable: true,
            v.IsSecret ? $"/api/v1/applications/{id}/environment/{v.Key}/reveal" : null,
            new DateTimeOffset(v.UpdatedAt, TimeSpan.Zero))));

        return TypedResults.Ok<IReadOnlyList<EnvironmentEntryDto>>(entries);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SetEnvironmentAsync(
        Guid id,
        string key,
        SetEnvironmentRequest request,
        ApplicationService service,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = CurrentUserId(http);
        var result = await service.SetEnvironmentAsync(id, key, request, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Failure!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.SecretChanged,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "application",
            ResourceId = id,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // The key, never the value.
                ["key"] = key,
                ["isSecret"] = request.IsSecret,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<RevealedSecretDto>, ProblemHttpResult>> RevealAsync(
        Guid id,
        string key,
        AirsideDbContext db,
        ISecretProtector protector,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var variable = await db.EnvironmentVariables
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.ApplicationId == id && v.Key == key, ct)
            .ConfigureAwait(false);

        if (variable is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such variable.").ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.SecretRevealed,
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "application",
            ResourceId = id,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http.Request.Headers.UserAgent.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key },
        }, ct).ConfigureAwait(false);

        if (!variable.IsSecret)
        {
            return TypedResults.Ok(new RevealedSecretDto(variable.Value));
        }

        var revealed = protector.Unprotect(variable.Value);

        return revealed.IsFailure
            ? revealed.Failure!.ToProblem()
            : TypedResults.Ok(new RevealedSecretDto(revealed.Value.Reveal()));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteEnvironmentAsync(
        Guid id,
        string key,
        AirsideDbContext db,
        CancellationToken ct)
    {
        var variable = await db.EnvironmentVariables
            .FirstOrDefaultAsync(v => v.ApplicationId == id && v.Key == key, ct)
            .ConfigureAwait(false);

        if (variable is null)
        {
            return TypedResults.NotFound();
        }

        db.EnvironmentVariables.Remove(variable);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<AttachmentDto>>> ListAttachmentsAsync(
        Guid id,
        AirsideDbContext db,
        EnvironmentRenderer renderer,
        CancellationToken ct)
    {
        var rows = await db.DatabaseAttachments
            .AsNoTracking()
            .Where(a => a.ApplicationId == id && a.DetachedAt == null)
            .Join(db.Databases, a => a.DatabaseInstanceId, d => d.Id, (a, d) => new { Attachment = a, Database = d })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<AttachmentDto>>(
        [
            .. rows.Select(r => new AttachmentDto(
                r.Attachment.Id,
                r.Database.Id,
                r.Database.Slug,
                r.Database.Engine.ToString().ToLowerInvariant(),
                r.Attachment.EnvKeyPrefix,
                renderer.InjectedKeysFor(r.Database.Engine, r.Attachment.EnvKeyPrefix),
                new DateTimeOffset(r.Attachment.AttachedAt, TimeSpan.Zero))),
        ]);
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> AttachAsync(
        Guid id,
        AttachDatabaseRequest request,
        ApplicationService service,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        var result = await service.AttachAsync(id, request, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Failure!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.DatabaseAttached,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "application",
            ResourceId = id,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["databaseId"] = request?.DatabaseId,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Accepted(result.Value.StatusUrl, result.Value);
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> DetachAsync(
        Guid id,
        Guid attachmentId,
        ApplicationService service,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        var result = await service.DetachAsync(id, attachmentId, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Failure!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.DatabaseDetached,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "application",
            ResourceId = id,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.Accepted(result.Value.StatusUrl, result.Value);
    }

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
