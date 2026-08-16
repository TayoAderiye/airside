using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Databases;
using Airside.Core.Jobs;
using Airside.Core.Naming;
using Airside.Core.Queries;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Jobs;
using Airside.Runtime.Queries;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Databases;

internal static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var databases = app.MapGroup("/api/v1/databases/{id:guid}").WithTags("Backups").RequireAuthorization();

        databases.MapGet("/backups", ListAsync).RequirePermission(Permissions.DatabaseRead);
        databases.MapPost("/backups", CreateAsync).RequirePermission(Permissions.DatabaseBackup);

        databases.MapGet("/credentials", ListCredentialsAsync).RequirePermission(Permissions.DatabaseRead);
        databases.MapPost("/credentials/rotate", RotateAsync)
            .RequirePermission(Permissions.DatabaseRotateCredentials);
        databases.MapPost("/credentials/{credentialId:guid}/reveal", RevealAsync)
            .RequirePermission(Permissions.SecretRead);
        databases.MapPost("/credentials/{credentialId:guid}/revoke", RevokeAsync)
            .RequirePermission(Permissions.DatabaseRotateCredentials);

        databases.MapPost("/query", QueryAsync).RequirePermission(Permissions.DatabaseQuery);
        databases.MapGet("/schema", SchemaAsync).RequirePermission(Permissions.DatabaseQuery);
        databases.MapGet("/query/history", HistoryAsync).RequirePermission(Permissions.DatabaseQuery);

        var backups = app.MapGroup("/api/v1/backups/{backupId:guid}").WithTags("Backups").RequireAuthorization();

        backups.MapGet("/", GetBackupAsync).RequirePermission(Permissions.DatabaseRead);
        backups.MapGet("/restore-preview", PreviewRestoreAsync).RequirePermission(Permissions.DatabaseRead);
        backups.MapPost("/restore", RestoreAsync)
            .RequirePermission(Permissions.DatabaseRestore)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<BackupDto>>, NotFound>> ListAsync(
        Guid id,
        AirsideDbContext db,
        CancellationToken ct)
    {
        // Answered before the query rather than after. An unknown id used to
        // return an empty list, which reads as "this database has no backups"
        // rather than "there is no such database" — and the two deserve
        // different answers on a screen that offers to restore from one.
        var exists = await db.Databases
            .AsNoTracking()
            .AnyAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);

        if (!exists)
        {
            return TypedResults.NotFound();
        }

        var backups = await db.Backups
            .AsNoTracking()
            .Where(b => b.DatabaseInstanceId == id)
            .OrderByDescending(b => b.StartedAt)
            .Take(100)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<BackupDto>>([.. backups.Select(BackupDto.From)]);
    }

    private static async Task<Results<Ok<BackupDto>, NotFound>> GetBackupAsync(
        Guid backupId,
        AirsideDbContext db,
        CancellationToken ct)
    {
        var backup = await db.Backups.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == backupId, ct).ConfigureAwait(false);

        return backup is null ? TypedResults.NotFound() : TypedResults.Ok(BackupDto.From(backup));
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> CreateAsync(
        Guid id,
        AirsideDbContext db,
        IDatabaseEngineRegistry engines,
        IJobQueue jobs,
        IAuditWriter audit,
        TimeProvider timeProvider,
        Microsoft.Extensions.Options.IOptions<AirsideStoreOptions> storeOptions,
        HttpContext http,
        CancellationToken ct)
    {
        var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);

        if (database is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such database.").ToProblem();
        }

        var capabilities = engines.Get(database.Engine).Capabilities;

        if (!capabilities.SupportsLogicalBackup && !capabilities.SupportsSnapshotBackup)
        {
            return new Error(
                ErrorCodes.BackupNotSupportedForEngine,
                $"{database.Engine} cannot be backed up.").ToProblem();
        }

        var backup = new Backup
        {
            DatabaseInstanceId = id,
            Kind = capabilities.SupportsSnapshotBackup ? BackupKind.Snapshot : BackupKind.Logical,
            TriggerKind = BackupTriggerKind.Manual,
            StoragePath = BackupStore.BackupPath(storeOptions.Value.BackupRoot, database),
            EngineSnapshot = $"{database.Engine.ToString().ToLowerInvariant()}:{database.Version}",
            DatabaseNameSnapshot = database.DatabaseName,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
            CreatedByUserId = CurrentUserId(http),
        };

        db.Backups.Add(backup);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            BackupJobTypes.Backup,
            new BackupPayload(id, backup.Id, nameof(BackupTriggerKind.Manual)),
            id,
            CurrentUserId(http),
            $"{BackupJobTypes.Backup}:{backup.Id}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.DatabaseBackedUp,
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "database",
            ResourceId = id,
            ResourceSlugSnapshot = database.Slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, BackupJobTypes.Backup, id);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>
    /// Tells the operator what a restore will actually do, before they commit.
    /// </summary>
    /// <remarks>
    /// <c>requiresStop</c> is the field that matters. A Redis restore is real
    /// downtime — an RDB cannot be loaded into a running instance — and finding
    /// that out from a progress bar rather than a confirmation dialog is how
    /// unplanned outages happen.
    /// </remarks>
    private static async Task<Results<Ok<RestorePreviewDto>, ProblemHttpResult>> PreviewRestoreAsync(
        Guid backupId,
        AirsideDbContext db,
        IDatabaseEngineRegistry engines,
        CancellationToken ct)
    {
        var backup = await db.Backups.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == backupId, ct).ConfigureAwait(false);

        if (backup is null)
        {
            return new Error(ErrorCodes.BackupNotFound, "No such backup.").ToProblem();
        }

        var database = await db.Databases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == backup.DatabaseInstanceId, ct).ConfigureAwait(false);

        if (database is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The database no longer exists.").ToProblem();
        }

        var capabilities = engines.Get(database.Engine).Capabilities;
        var target = $"{database.Engine.ToString().ToLowerInvariant()}:{database.Version}";
        var compatibility = BackupVerificationBridge.Check(backup.EngineSnapshot, target);

        return TypedResults.Ok(new RestorePreviewDto(
            capabilities.RequiresStopForRestore,
            capabilities.RequiresStopForRestore ? EstimateDowntimeSeconds(backup.SizeBytes) : null,
            compatibility,
            backup.EngineSnapshot,
            target,
            PreRestoreBackupWillBeTaken: true));
    }

    private static int EstimateDowntimeSeconds(long? sizeBytes) =>
        // Deliberately coarse and deliberately generous. An estimate that reads
        // as precise invites planning around it.
        sizeBytes is null ? 60 : (int)Math.Clamp(30 + (sizeBytes.Value / (50L * 1024 * 1024)), 30, 1800);

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> RestoreAsync(
        Guid backupId,
        RestoreRequest request,
        AirsideDbContext db,
        IJobQueue jobs,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var backup = await db.Backups.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == backupId, ct).ConfigureAwait(false);

        if (backup is null)
        {
            return new Error(ErrorCodes.BackupNotFound, "No such backup.").ToProblem();
        }

        var database = await db.Databases
            .FirstOrDefaultAsync(d => d.Id == backup.DatabaseInstanceId, ct).ConfigureAwait(false);

        if (database is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The database no longer exists.").ToProblem();
        }

        if (!string.Equals(request.ConfirmSlug, database.Slug, StringComparison.Ordinal))
        {
            return new Error(
                ErrorCodes.WorkloadConfirmationMismatch,
                "Type the database's name exactly to confirm the restore.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["expected"] = database.Slug })
                .ToProblem();
        }

        var restore = new Restore
        {
            DatabaseInstanceId = database.Id,
            BackupId = backupId,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
            RequestedByUserId = CurrentUserId(http),
        };

        db.Restores.Add(restore);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            BackupJobTypes.Restore,
            new RestorePayload(database.Id, restore.Id, backupId),
            database.Id,
            CurrentUserId(http),
            $"{BackupJobTypes.Restore}:{restore.Id}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.DatabaseRestored,
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "database",
            ResourceId = database.Id,
            ResourceSlugSnapshot = database.Slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["backupId"] = backupId,
                ["backupTakenAt"] = backup.StartedAt,
            },
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, BackupJobTypes.Restore, database.Id);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }

    private static async Task<Ok<IReadOnlyList<CredentialDto>>> ListCredentialsAsync(
        Guid id,
        AirsideDbContext db,
        CancellationToken ct)
    {
        var credentials = await db.DatabaseCredentials
            .AsNoTracking()
            .Where(c => c.DatabaseInstanceId == id)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Masked. The value is never in a list response, only behind an explicit,
        // separately permissioned, audited reveal.
        return TypedResults.Ok<IReadOnlyList<CredentialDto>>(
        [
            .. credentials.Select(c => new CredentialDto(
                c.Id,
                c.Username,
                new SecretFieldDto(true, Secret.Mask, $"/api/v1/databases/{id}/credentials/{c.Id}/reveal",
                    new DateTimeOffset(c.UpdatedAt, TimeSpan.Zero)),
                c.IsPrimary,
                c.State.ToString().ToLowerInvariant(),
                new DateTimeOffset(c.CreatedAt, TimeSpan.Zero))),
        ]);
    }

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> RotateAsync(
        Guid id,
        AirsideDbContext db,
        IJobQueue jobs,
        ISecretProtector protector,
        ISecretGenerator generator,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var database = await db.Databases
            .Include(d => d.Credentials)
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);

        if (database is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such database.").ToProblem();
        }

        var current = database.Credentials.FirstOrDefault(c => c.IsPrimary && c.State == CredentialState.Active);

        if (current is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "This database has no active credential.").ToProblem();
        }

        // Written as Retired until the engine confirms it. A credential row that
        // claims to be active before the database agrees would hand applications a
        // password that does not work.
        var replacement = new DatabaseCredential
        {
            DatabaseInstanceId = id,
            Username = current.Username,
            EncryptedPassword = protector.Protect(generator.GeneratePassword()),
            IsPrimary = false,
            State = CredentialState.Retired,
            RotatedByUserId = CurrentUserId(http),
        };

        db.DatabaseCredentials.Add(replacement);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            BackupJobTypes.RotateCredentials,
            new RotateCredentialsPayload(id, replacement.Id),
            id,
            CurrentUserId(http),
            $"{BackupJobTypes.RotateCredentials}:{replacement.Id}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.CredentialsRotated,
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "database",
            ResourceId = id,
            ResourceSlugSnapshot = database.Slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, BackupJobTypes.RotateCredentials, id);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }

    private static async Task<Results<Ok<RevealedSecretDto>, ProblemHttpResult>> RevealAsync(
        Guid id,
        Guid credentialId,
        AirsideDbContext db,
        ISecretProtector protector,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var credential = await db.DatabaseCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.DatabaseInstanceId == id, ct)
            .ConfigureAwait(false);

        if (credential is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such credential.").ToProblem();
        }

        var revealed = protector.Unprotect(credential.EncryptedPassword);

        // Audited before the value is returned, so a reveal that reaches the
        // client is always one the log already knows about.
        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.SecretRevealed,
            Result = revealed.IsSuccess ? AuditResult.Success : AuditResult.Failure,
            UserId = CurrentUserId(http),
            ResourceKind = "database_credential",
            ResourceId = credentialId,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http.Request.Headers.UserAgent.ToString(),
        }, ct).ConfigureAwait(false);

        return revealed.IsFailure
            ? revealed.Failure!.ToProblem()
            : TypedResults.Ok(new RevealedSecretDto(revealed.Value.Reveal()));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeAsync(
        Guid id,
        Guid credentialId,
        AirsideDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var credential = await db.DatabaseCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.DatabaseInstanceId == id, ct)
            .ConfigureAwait(false);

        if (credential is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such credential.").ToProblem();
        }

        if (credential.IsPrimary)
        {
            // Revoking the live credential would leave the database with no
            // usable password at all, and Airside would lose its own access.
            return new Error(
                ErrorCodes.ValidationFailed,
                "This is the active credential and revoking it would leave the database unreachable. "
                + "Rotate to issue a replacement instead.").ToProblem();
        }

        credential.State = CredentialState.Revoked;
        credential.RetiredAt ??= timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<QueryResponseDto>, ProblemHttpResult>> QueryAsync(
        Guid id,
        QueryRequestDto request,
        AirsideDbContext db,
        IQueryConsoleFactory consoles,
        ISecretProtector protector,
        ControlPlaneQueryTarget controlPlane,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var database = await db.Databases
            .Include(d => d.Credentials)
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);

        var resolved = await ResolveQueryTargetAsync(database, id, protector, controlPlane, ct)
            .ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            return resolved.Failure!.ToProblem();
        }

        var (endpoint, credentialValue, engine, slug, isControlPlane) = resolved.Value;

        var userId = CurrentUserId(http);

        var console = consoles.Create(engine, credentialValue.Username);

        var execution = new QueryExecution(
            endpoint,
            credentialValue,
            request.Statement,
            Math.Clamp(request.MaxRows ?? 500, 1, 5000),
            TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds ?? 30, 1, 300)),
            http.User.HasClaim(AirsideClaims.Permission, Permissions.DatabaseQueryDestructive));

        var started = timeProvider.GetUtcNow().UtcDateTime;
        var result = await console.ExecuteAsync(execution, ct).ConfigureAwait(false);

        // History rows carry a foreign key to a workload, and the control-plane
        // store is not one. Skipped rather than faked with an id that matches no
        // row, which is what made these ids safe in the first place.
        if (userId is { } uid && !isControlPlane)
        {
            await RecordHistoryAsync(db, uid, id, request.Statement, started, result, timeProvider, ct)
                .ConfigureAwait(false);
        }

        await audit.WriteAsync(new AuditEntry
        {
            // A distinct action for the control-plane store, so "who read the
            // table holding every credential on this host" is a question the
            // audit log can answer without joining against workload ids.
            Action = isControlPlane ? "query.control_plane_executed" : AuditActions.QueryExecuted,
            Result = result.IsSuccess ? AuditResult.Success : AuditResult.Denied,
            UserId = userId,
            ResourceKind = isControlPlane ? "control_plane" : "database",
            ResourceId = id,
            ResourceSlugSnapshot = slug,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // The statement itself is never audited: it can contain literal
                // secrets, and the audit log is readable by a different permission
                // than the query console.
                ["outcome"] = result.IsSuccess ? "executed" : result.Failure!.Code,
            },
        }, ct).ConfigureAwait(false);

        return result.IsFailure
            ? result.Failure!.ToProblem()
            : TypedResults.Ok(new QueryResponseDto(
                Guid.CreateVersion7(),
                result.Value.Columns,
                result.Value.Rows,
                result.Value.RowsAffected,
                result.Value.Truncated,
                (int)result.Value.Duration.TotalMilliseconds));
    }

    /// <summary>
    /// The tables and columns available to query.
    /// </summary>
    /// <remarks>
    /// Behind <c>database.query</c> rather than <c>database.read</c>: knowing a
    /// schema is knowing what is stored, which is closer to reading the contents
    /// than to seeing that a database exists.
    /// </remarks>
    private static async Task<Results<Ok<DatabaseSchemaDto>, ProblemHttpResult>> SchemaAsync(
        Guid id,
        AirsideDbContext db,
        IQueryConsoleFactory consoles,
        ISecretProtector protector,
        ControlPlaneQueryTarget controlPlane,
        CancellationToken ct)
    {
        var database = await db.Databases
            .Include(d => d.Credentials)
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);

        var resolved = await ResolveQueryTargetAsync(database, id, protector, controlPlane, ct)
            .ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            return resolved.Failure!.ToProblem();
        }

        var target = resolved.Value;
        var console = consoles.Create(target.Engine, target.Credential.Username);

        var schema = await console.DescribeAsync(
            new QueryExecution(
                target.Endpoint,
                target.Credential,
                string.Empty,
                MaxRows: 20_000,
                TimeSpan.FromSeconds(30),
                CallerHasDestructivePermission: false),
            ct).ConfigureAwait(false);

        if (schema.IsFailure)
        {
            return schema.Failure!.ToProblem();
        }

        return TypedResults.Ok(new DatabaseSchemaDto(
            [
                .. schema.Value.Tables.Select(t => new SchemaTableDto(
                    t.Namespace,
                    t.Name,
                    [
                        .. t.Columns.Select(c => new SchemaColumnDto(
                            c.Name, c.DataType, c.Nullable, c.IsPrimaryKey)),
                    ])),
            ]));
    }

    /// <summary>
    /// Everything needed to reach a database's console, whichever kind it is.
    /// </summary>
    private readonly record struct QueryTarget(
        DatabaseEndpoint Endpoint,
        DatabaseCredentialValue Credential,
        DatabaseEngineKind Engine,
        string Slug,
        bool IsControlPlane);

    /// <summary>
    /// Resolves a workload id to a console target: a managed database, or
    /// Airside's own store.
    /// </summary>
    /// <remarks>
    /// Shared by the query endpoint and the schema browser, because the two must
    /// agree about what they are pointed at. A browser that resolved differently
    /// from the console beside it would list one database's tables and run
    /// statements against another.
    /// </remarks>
    private static async Task<Result<QueryTarget>> ResolveQueryTargetAsync(
        DatabaseInstance? database,
        Guid id,
        ISecretProtector protector,
        ControlPlaneQueryTarget controlPlane,
        CancellationToken ct)
    {
        if (database?.ContainerId is not null)
        {
            var credential = database.Credentials.First(c => c.IsPrimary && c.State == CredentialState.Active);
            var password = protector.Unprotect(credential.EncryptedPassword);

            if (password.IsFailure)
            {
                return password.Failure!;
            }

            return new QueryTarget(
                new DatabaseEndpoint(database.ContainerId, database.Slug, 0, database.DatabaseName),
                new DatabaseCredentialValue(credential.Username, password.Value),
                database.Engine,
                database.Slug,
                IsControlPlane: false);
        }

        if (SystemWorkloadReader.ResolveContainerName(id) != AirsideLabels.SystemContainers.Database)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such running database.");
        }

        // Airside's own store. Reachable because refusing withheld nothing — the
        // documented recovery path is a psql shell on the host — while costing
        // the operator the tool that would have answered their question.
        var target = await controlPlane.ResolveAsync(ct).ConfigureAwait(false);

        if (target.IsFailure)
        {
            return target.Failure!;
        }

        return new QueryTarget(
            target.Value.Endpoint,
            target.Value.Credential,
            ControlPlaneQueryTarget.Engine,
            AirsideLabels.SystemContainers.Database,
            IsControlPlane: true);
    }

    private const int HistoryPerDatabase = 100;

    private static async Task RecordHistoryAsync(
        AirsideDbContext db,
        Guid userId,
        Guid databaseId,
        string statement,
        DateTime started,
        Result<Core.Queries.QueryOutcome> result,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        db.QueryHistory.Add(new QueryHistoryEntry
        {
            UserId = userId,
            DatabaseInstanceId = databaseId,
            Body = statement.Length > 64 * 1024 ? statement[..(64 * 1024)] : statement,
            ExecutedAt = started,
            DurationMs = (int)(timeProvider.GetUtcNow().UtcDateTime - started).TotalMilliseconds,
            RowsAffected = result.IsSuccess ? result.Value.RowsAffected : 0,
            Success = result.IsSuccess,
            ErrorMessage = result.IsFailure ? result.Failure!.Code : null,
        });

        // Pruned on write. History holds statements that can contain literal
        // secrets, so it is capped rather than allowed to accumulate for ever.
        var stale = await db.QueryHistory
            .Where(h => h.UserId == userId && h.DatabaseInstanceId == databaseId)
            .OrderByDescending(h => h.ExecutedAt)
            .Skip(HistoryPerDatabase)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.QueryHistory.RemoveRange(stale);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// History is strictly the caller's own.
    /// </summary>
    /// <remarks>
    /// There is no parameter for whose history to read, deliberately. A statement
    /// like <c>INSERT INTO users (password) VALUES ('…')</c> lands here verbatim,
    /// so one operator reading another's history would be a credential
    /// disclosure — regardless of how senior they are.
    /// </remarks>
    private static async Task<Ok<IReadOnlyList<QueryHistoryDto>>> HistoryAsync(
        Guid id,
        AirsideDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);

        var entries = await db.QueryHistory
            .AsNoTracking()
            .Where(h => h.DatabaseInstanceId == id && h.UserId == userId)
            .OrderByDescending(h => h.ExecutedAt)
            .Take(HistoryPerDatabase)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<QueryHistoryDto>>(
        [
            .. entries.Select(h => new QueryHistoryDto(
                h.Id, h.Body, new DateTimeOffset(h.ExecutedAt, TimeSpan.Zero),
                h.DurationMs, h.RowsAffected, h.Success, h.ErrorMessage)),
        ]);
    }

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

/// <summary>Exposes the runtime's version check to the API without widening its surface.</summary>
internal static class BackupVerificationBridge
{
    public static bool Check(string backupSnapshot, string targetSnapshot) =>
        Airside.Runtime.Databases.BackupVerification
            .CheckEngineCompatibility(backupSnapshot, targetSnapshot).IsSuccess;
}
