using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Databases;
using Airside.Core.Security;
using Airside.Core.Workloads;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Databases;

internal static class DatabaseEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/database-engines", ListEnginesAsync)
            .WithTags("Databases")
            .RequirePermission(Permissions.DatabaseRead);

        var group = app.MapGroup("/api/v1/databases").WithTags("Databases").RequireAuthorization();

        group.MapGet("/", ListAsync).RequirePermission(Permissions.DatabaseRead);
        group.MapGet("/{id:guid}", GetAsync).RequirePermission(Permissions.DatabaseRead);
        group.MapPost("/", CreateAsync).RequirePermission(Permissions.DatabaseCreate);
        group.MapPost("/{id:guid}/resize", ResizeAsync).RequirePermission(Permissions.DatabaseUpdate);

        group.MapPost("/{id:guid}/start", (Guid id, DatabaseService s, HttpContext h, CancellationToken ct) =>
                LifecycleAsync(id, DatabaseJobTypes.Start, DatabaseState.Running, s, h, ct))
            .RequirePermission(Permissions.DatabaseLifecycle);

        group.MapPost("/{id:guid}/stop", (Guid id, DatabaseService s, HttpContext h, CancellationToken ct) =>
                LifecycleAsync(id, DatabaseJobTypes.Stop, DatabaseState.Stopped, s, h, ct))
            .RequirePermission(Permissions.DatabaseLifecycle);

        group.MapPost("/{id:guid}/restart", (Guid id, DatabaseService s, HttpContext h, CancellationToken ct) =>
                LifecycleAsync(id, DatabaseJobTypes.Restart, DatabaseState.Restarting, s, h, ct))
            .RequirePermission(Permissions.DatabaseLifecycle);

        // POST rather than DELETE: this carries a body (the typed confirmation and
        // the volume decision), and bodies on DELETE are unreliable through
        // intermediaries.
        group.MapPost("/{id:guid}/delete", DeleteAsync)
            .RequirePermission(Permissions.DatabaseDelete)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        app.MapGet("/api/v1/volumes", ListVolumesAsync)
            .WithTags("Volumes")
            .RequirePermission(Permissions.ServerManage);

        return app;
    }

    /// <summary>
    /// The engine catalogue.
    /// </summary>
    /// <remarks>
    /// Load-bearing for the UI: the provisioning form is rendered from these
    /// capabilities rather than from hardcoded engine knowledge. When
    /// <c>supportsDatabaseName</c> is false the field is not shown; when
    /// <c>requiresMaxMemory</c> is true the Redis fields appear and are required.
    /// That is what stops Redis becoming a pile of engine checks in the frontend —
    /// the same mistake the backend is built to avoid.
    /// </remarks>
    private static Ok<IReadOnlyList<DatabaseEngineDto>> ListEnginesAsync(IDatabaseEngineRegistry engines)
    {
        var catalogue = engines.All.Select(e => new DatabaseEngineDto(
            e.Kind.ToString().ToLowerInvariant(),
            DisplayNameFor(e.Kind),
            e.SupportedVersions,
            e.SupportedVersions[0],
            e.Capabilities.DefaultPort,
            new DatabaseCapabilitiesDto(
                e.Capabilities.SupportsDatabaseName,
                e.Capabilities.SupportsUserAccounts,
                e.Capabilities.SupportsLogicalBackup,
                e.Capabilities.SupportsSnapshotBackup,
                e.Capabilities.RequiresStopForRestore,
                e.Capabilities.RequiresMaxMemory,
                char.ToLowerInvariant(e.Capabilities.QueryDialect.ToString()[0])
                    + e.Capabilities.QueryDialect.ToString()[1..],
                e.Capabilities.DefaultEnvKeyPrefix),
            e.Capabilities.EvictionPolicies,
            InjectedKeysFor(e),
            VariantsFor(e))).ToList();

        return TypedResults.Ok<IReadOnlyList<DatabaseEngineDto>>(catalogue);
    }

    /// <summary>
    /// The variant choices for an engine, with guidance only where it is warranted.
    /// </summary>
    /// <remarks>
    /// The note hangs off the non-default option. Airside defaults to Alpine where
    /// upstream publishes one, so the thing worth saying is what picking Debian
    /// buys and costs — not a warning on the path almost everyone takes.
    /// </remarks>
    private static IReadOnlyList<ImageVariantDto> VariantsFor(IDatabaseEngine engine) =>
    [
        .. engine.Capabilities.SupportedVariants.Select(v => new ImageVariantDto(
            v.ToString().ToLowerInvariant(),
            v == ImageVariant.Alpine ? "Alpine" : "Debian",
            v == engine.Capabilities.DefaultVariant,
            v == engine.Capabilities.DefaultVariant ? null : NoteFor(v))),
    ];

    private static string? NoteFor(ImageVariant variant) => variant switch
    {
        ImageVariant.Debian =>
            "Larger image, but broader extension availability and standard glibc tooling. "
            + "The variant cannot be changed after the database is created.",
        ImageVariant.Alpine =>
            "Smaller image built on musl libc with a BusyBox userland. "
            + "The variant cannot be changed after the database is created.",
        _ => null,
    };

    private static string DisplayNameFor(DatabaseEngineKind kind) => kind switch
    {
        DatabaseEngineKind.Postgres => "PostgreSQL",
        DatabaseEngineKind.MySql => "MySQL",
        DatabaseEngineKind.MongoDb => "MongoDB",
        DatabaseEngineKind.Redis => "Redis",
        _ => kind.ToString(),
    };

    /// <summary>Derived from the engine itself, so the documented keys cannot drift from the injected ones.</summary>
    private static IReadOnlyList<string> InjectedKeysFor(IDatabaseEngine engine)
    {
        var sample = new ConnectionDetails(
            "host", engine.Capabilities.DefaultPort,
            engine.Capabilities.SupportsDatabaseName ? "name" : null,
            engine.Capabilities.SupportsUserAccounts ? "user" : null,
            new Core.Common.Secret("password"),
            new Core.Common.Secret("url"));

        return [.. engine.BuildInjectedEnvironment(engine.Capabilities.DefaultEnvKeyPrefix, sample)
            .Select(e => e.Key)];
    }

    private static async Task<Ok<PagedResult<DatabaseSummaryDto>>> ListAsync(
        AirsideDbContext db,
        SystemWorkloadReader system,
        CancellationToken ct,
        int page = 1,
        int pageSize = 25)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var total = await db.Databases.CountAsync(ct).ConfigureAwait(false);

        var databases = await db.Databases
            .AsNoTracking()
            .Include(d => d.Volumes)
            .OrderBy(d => d.Slug)
            .Skip((Math.Max(1, page) - 1) * size)
            .Take(size)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The control-plane store, on the first page only. Absent entirely under
        // the SQLite provider, where there is no database container to find.
        var systemDatabases = Math.Max(1, page) == 1
            ? await system.DatabasesAsync(ct).ConfigureAwait(false)
            : [];

        return TypedResults.Ok(new PagedResult<DatabaseSummaryDto>(
            [.. systemDatabases, .. databases.Select(DatabaseSummaryDto.From)],
            Math.Max(1, page),
            size,
            total));
    }

    private static async Task<Results<Ok<DatabaseDetailDto>, NotFound>> GetAsync(
        Guid id,
        AirsideDbContext db,
        IDatabaseEngineRegistry engines,
        CancellationToken ct)
    {
        var database = await db.Databases
            .AsNoTracking()
            .Include(d => d.Volumes)
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);

        return database is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(DatabaseDetailDto.From(
                database, DatabaseService.WarningsFor(database, engines.Get(database.Engine))));
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> CreateAsync(
        CreateDatabaseRequest request,
        DatabaseService service,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        var result = await service.ProvisionAsync(request, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Failure!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.DatabaseProvisioned,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "database",
            ResourceId = result.Value.WorkloadId,
            ResourceSlugSnapshot = request.Slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["engine"] = request.Engine,
                ["version"] = request.Version,
                // Never the password, generated or supplied.
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Accepted(result.Value.StatusUrl, result.Value);
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> LifecycleAsync(
        Guid id,
        string jobType,
        DatabaseState transitionalState,
        DatabaseService service,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await service
            .LifecycleAsync(id, jobType, transitionalState, CurrentUserId(http), ct)
            .ConfigureAwait(false);

        return result.IsFailure
            ? result.Failure!.ToProblem()
            : TypedResults.Accepted(result.Value.StatusUrl, result.Value);
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> ResizeAsync(
        Guid id,
        ResizeDatabaseRequest request,
        DatabaseService service,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        var result = await service.ResizeAsync(id, request, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Failure!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.DatabaseResized,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "database",
            ResourceId = id,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cpuNanos"] = request.CpuNanos,
                ["memoryBytes"] = request.MemoryBytes,
                ["storageBytes"] = request.StorageBytes,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Accepted(result.Value.StatusUrl, result.Value);
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> DeleteAsync(
        Guid id,
        DeleteDatabaseRequest request,
        DatabaseService service,
        IAuditWriter audit,
        AirsideDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = CurrentUserId(http);
        var slug = await db.Databases
            .AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => d.Slug)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var result = await service.DeleteAsync(id, request, userId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            // A failed confirmation is audited too: repeated near-misses on a
            // delete confirmation are worth being able to see.
            await audit.WriteAsync(new AuditEntry
            {
                Action = AuditActions.DatabaseDeleted,
                Result = AuditResult.Denied,
                UserId = userId,
                ResourceKind = "database",
                ResourceId = id,
                ResourceSlugSnapshot = slug,
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reason"] = result.Failure!.Code,
                },
            }, ct).ConfigureAwait(false);

            return result.Failure.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.DatabaseDeleted,
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "database",
            ResourceId = id,
            ResourceSlugSnapshot = slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // The single most consequential flag in the product.
                ["deleteVolume"] = request.DeleteVolume,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Accepted(result.Value.StatusUrl, result.Value);
    }

    private static async Task<Ok<IReadOnlyList<VolumeDto>>> ListVolumesAsync(
        AirsideDbContext db,
        CancellationToken ct,
        bool? orphaned = null)
    {
        // IgnoreQueryFilters: an orphaned volume's workload is soft-deleted, and
        // the whole point of the reclaim screen is saying which database it came
        // from.
        var query = db.Volumes.AsNoTracking().Include(v => v.Workload).IgnoreQueryFilters()
            .Where(v => v.DeletedAt == null);

        if (orphaned == true)
        {
            query = query.Where(v => v.OrphanedAt != null);
        }
        else if (orphaned == false)
        {
            query = query.Where(v => v.OrphanedAt == null);
        }

        var volumes = await query.OrderBy(v => v.Name).ToListAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<VolumeDto>>(
        [
            .. volumes.Select(v => new VolumeDto(
                v.Id, v.Name, v.WorkloadId, v.Workload.Slug,
                v.Purpose.ToString().ToLowerInvariant(),
                v.SizeAllocationBytes, v.LastMeasuredBytes, v.MeasuredAt, v.OrphanedAt)),
        ]);
    }

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
