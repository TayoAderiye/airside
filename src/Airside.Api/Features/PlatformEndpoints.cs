using Airside.Api.Contracts;
using Airside.Api.Hosting;
using Airside.Api.Security;
using Airside.Core.Containers;
using Airside.Core.Hosting;
using Airside.Core.Jobs;
using Airside.Core.Security;
using Airside.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features;

internal static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapHostEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/host").WithTags("Host").RequireAuthorization();
        group.MapGet("/", GetHostAsync).RequirePermission(Permissions.MetricsRead);
        return app;
    }

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/jobs").WithTags("Jobs").RequireAuthorization();

        group.MapGet("/", ListJobsAsync);
        group.MapGet("/{id:guid}", GetJobAsync);
        group.MapPost("/{id:guid}/cancel", CancelJobAsync);

        return app;
    }

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/audit", ListAuditAsync)
            .WithTags("Audit")
            .RequirePermission(Permissions.AuditRead);

        return app;
    }

    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/permissions", ListPermissionsAsync)
            .WithTags("Access")
            .RequirePermission(Permissions.RoleManage);

        app.MapGet("/api/v1/roles", ListRolesAsync)
            .WithTags("Access")
            .RequirePermission(Permissions.RoleManage);

        app.MapGet("/api/v1/users", ListUsersAsync)
            .WithTags("Access")
            .RequirePermission(Permissions.UserManage);

        return app;
    }

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/system/info", GetSystemInfoAsync)
            .WithTags("System")
            .RequireAuthorization();

        // Anonymous on purpose. The dashboard ships as its own container and can
        // be a different version from the API, so it checks compatibility before
        // it renders anything — and it has to be able to do that while logged
        // out, because the login screen is precisely what breaks when the
        // contract has moved underneath it. Gating this behind authentication
        // would mean the check only runs once it is too late to be useful.
        //
        // Nothing is disclosed that was not already public: /api/v1/setup/status
        // is anonymous and returns the same version, and does a database read to
        // do it, where this returns a string read once at startup.
        app.MapGet("/api/v1/version", GetVersion)
            .WithTags("System")
            .AllowAnonymous();

        return app;
    }

    /// <summary>The running API version. See <see cref="VersionDto"/> — the shape is frozen.</summary>
    private static Ok<VersionDto> GetVersion() =>
        TypedResults.Ok(new VersionDto(BuildInfo.Version));

    private static async Task<Ok<HostDto>> GetHostAsync(
        AirsideDbContext db,
        IHostAllocationReader allocation,
        CancellationToken ct)
    {
        var host = await db.Hosts.AsNoTracking().FirstAsync(ct).ConfigureAwait(false);
        var position = await allocation.ReadPositionAsync(ct).ConfigureAwait(false);

        var available = new ResourceTripleDto(
            Math.Max(0, position.Capacity.CpuNanos - position.Reserve.CpuNanos - position.Allocated.CpuNanos),
            Math.Max(0, position.Capacity.MemoryBytes - position.Reserve.MemoryBytes - position.Allocated.MemoryBytes),
            Math.Max(0, position.Capacity.StorageBytes - position.Reserve.StorageBytes - position.Allocated.StorageBytes));

        var warnings = new List<WarningDto>();

        if (position.StorageEnforcement == StorageEnforcement.Accounting)
        {
            warnings.Add(new WarningDto(
                "storage.enforcement_unavailable",
                "This host's filesystem cannot enforce per-volume size limits. Storage allocation is "
                + "counted and alerted on, but a workload can still fill the disk."));
        }

        return TypedResults.Ok(new HostDto(
            host.Id,
            host.Name,
            new ResourceTripleDto(position.Capacity.CpuNanos, position.Capacity.MemoryBytes, position.Capacity.StorageBytes),
            new ResourceTripleDto(position.Reserve.CpuNanos, position.Reserve.MemoryBytes, position.Reserve.StorageBytes),
            ResourceTripleDto.From(position.Allocated),
            position.Used is null ? null : ResourceTripleDto.From(position.Used),
            available,
            position.StorageEnforcement.ToString().ToLowerInvariant(),
            host.DockerApiVersion,
            host.KernelVersion,
            host.OperatingSystem,
            host.LastDiscoveredAt,
            warnings));
    }

    private static async Task<Ok<CursorResult<JobDto>>> ListJobsAsync(
        AirsideDbContext db,
        CancellationToken ct,
        Guid? workloadId = null,
        string? status = null,
        int limit = 50)
    {
        var query = db.Jobs.AsNoTracking().Include(j => j.Steps).AsQueryable();

        if (workloadId is not null)
        {
            query = query.Where(j => j.WorkloadId == workloadId);
        }

        if (Enum.TryParse<JobStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(j => j.Status == parsed);
        }

        var jobs = await query
            .OrderByDescending(j => j.QueuedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok(new CursorResult<JobDto>([.. jobs.Select(JobDto.From)], null));
    }

    private static async Task<Results<Ok<JobDto>, NotFound>> GetJobAsync(
        Guid id,
        AirsideDbContext db,
        CancellationToken ct)
    {
        var job = await db.Jobs
            .AsNoTracking()
            .Include(j => j.Steps)
            .FirstOrDefaultAsync(j => j.Id == id, ct)
            .ConfigureAwait(false);

        return job is null ? TypedResults.NotFound() : TypedResults.Ok(JobDto.From(job));
    }

    private static async Task<Results<Accepted, ProblemHttpResult>> CancelJobAsync(
        Guid id,
        IJobQueue queue,
        CancellationToken ct)
    {
        var result = await queue.RequestCancellationAsync(id, ct).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Accepted($"/api/v1/jobs/{id}")
            : Infrastructure.ProblemResults.ToProblem(result.Failure!);
    }

    /// <summary>
    /// Keyset pagination over an append-only log.
    /// </summary>
    /// <remarks>
    /// Offset paging would silently skip rows as new events arrive while the user
    /// is paging, and audit is the one place where a silently skipped row is the
    /// whole problem.
    /// </remarks>
    private static async Task<Ok<CursorResult<AuditEventDto>>> ListAuditAsync(
        AirsideDbContext db,
        CancellationToken ct,
        string? cursor = null,
        string? action = null,
        Guid? resourceId = null,
        int limit = 50)
    {
        var query = db.AuditEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(action))
        {
            query = query.Where(a => a.Action == action);
        }

        if (resourceId is not null)
        {
            query = query.Where(a => a.ResourceId == resourceId);
        }

        if (DateTimeOffset.TryParse(cursor, System.Globalization.CultureInfo.InvariantCulture, out var after))
        {
            // Keyset on the timestamp. Guid has no relational operator in LINQ, so
            // paging on the UUIDv7 id directly does not translate on either
            // provider. Two audit rows sharing a 100-nanosecond tick would collide;
            // at control-plane event rates that does not occur, and the ordering
            // stays stable as new rows arrive, which is the property that matters.
            query = query.Where(a => a.OccurredAt < after);
        }

        var take = Math.Clamp(limit, 1, 200);

        var events = await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(take + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = events.Count > take;
        var page = hasMore ? events[..take] : events;

        return TypedResults.Ok(new CursorResult<AuditEventDto>(
            [.. page.Select(a => new AuditEventDto(
                a.Id, a.OccurredAt, a.UserId, a.UserEmailSnapshot, a.Action,
                a.ResourceKind, a.ResourceId, a.ResourceSlugSnapshot,
                a.Result.ToString().ToLowerInvariant(), a.IpAddress, a.CorrelationId))],
            hasMore ? page[^1].OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : null));
    }

    private static async Task<Ok<IReadOnlyList<PermissionDto>>> ListPermissionsAsync(
        AirsideDbContext db,
        CancellationToken ct)
    {
        var permissions = await db.Permissions
            .AsNoTracking()
            .OrderBy(p => p.Code)
            .Select(p => new PermissionDto(p.Code, p.Description, p.IsObsolete))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<PermissionDto>>(permissions);
    }

    private static async Task<Ok<IReadOnlyList<RoleDto>>> ListRolesAsync(
        AirsideDbContext db,
        CancellationToken ct)
    {
        var roles = await db.Roles
            .AsNoTracking()
            .Include(r => r.RolePermissions)
            .OrderBy(r => r.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<RoleDto>>(
        [
            .. roles.Select(r => new RoleDto(
                r.Id, r.Slug, r.Name, r.Description, r.IsSystem,
                [.. r.RolePermissions.Select(rp => rp.PermissionCode).Order(StringComparer.Ordinal)])),
        ]);
    }

    private static async Task<Ok<PagedResult<UserDto>>> ListUsersAsync(
        AirsideDbContext db,
        CancellationToken ct,
        int page = 1,
        int pageSize = 25)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var skip = (Math.Max(1, page) - 1) * size;

        var total = await db.Users.CountAsync(ct).ConfigureAwait(false);

        var users = await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Email)
            .Skip(skip)
            .Take(size)
            .Select(u => new UserDto(
                u.Id,
                u.Email ?? string.Empty,
                u.DisplayName,
                u.IsActive,
                u.UserRoles.Select(ur => ur.Role.Slug).ToList(),
                u.LastLoginAt,
                u.CreatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok(new PagedResult<UserDto>(users, Math.Max(1, page), size, total));
    }

    private static async Task<Ok<SystemInfoDto>> GetSystemInfoAsync(
        AirsideDbContext db,
        IContainerRuntime runtime,
        CancellationToken ct)
    {
        var settings = await db.InstanceSettings.AsNoTracking().FirstAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new SystemInfoDto(
            BuildInfo.Version,
            settings.CurrentImageTag,
            settings.StoreProvider.ToString().ToLowerInvariant(),
            settings.InstanceName,
            await runtime.IsAvailableAsync(ct).ConfigureAwait(false),
            BuildInfo.StartedAt));
    }
}
