using System.Security.Claims;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Common;
using Airside.Core.Operations;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Operations;

public sealed record MetricPointDto(
    DateTimeOffset Hour,
    long CpuNanosAvg,
    long CpuNanosMax,
    long MemoryBytesAvg,
    long MemoryBytesMax,
    long MemoryLimitBytes,
    long NetworkRxBytes,
    long NetworkTxBytes,
    int SampleCount);

public sealed record NotificationDto(
    Guid Id,
    string Severity,
    string Title,
    string Body,
    string? Code,
    string? ResourceKind,
    Guid? ResourceId,
    int OccurrenceCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    bool Acknowledged,
    bool Resolved);

public sealed record StartUpdateRequest(string Version);

public sealed record UpdateRecordDto(
    Guid Id,
    string FromVersion,
    string ToVersion,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    string? BackupPath,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

internal static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/workloads/{id:guid}/metrics", GetMetricsAsync)
            .WithTags("Metrics")
            .RequirePermission(Permissions.MetricsRead);

        app.MapGet("/api/v1/notifications", ListNotificationsAsync)
            .WithTags("Notifications")
            .RequirePermission(Permissions.ApplicationRead);

        app.MapPost("/api/v1/notifications/{id:guid}/acknowledge", AcknowledgeAsync)
            .WithTags("Notifications")
            .RequirePermission(Permissions.ApplicationRead);

        app.MapGet("/api/v1/system/updates", ListUpdatesAsync)
            .WithTags("System")
            .RequirePermission(Permissions.ServerUpdate);

        app.MapPost("/api/v1/system/updates", StartUpdateAsync)
            .WithTags("System")
            .RequirePermission(Permissions.ServerUpdate)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        app.MapPost("/api/v1/system/backups", CreateSystemBackupAsync)
            .WithTags("System")
            .RequirePermission(Permissions.ServerManage);

        return app;
    }

    /// <param name="hours">
    /// How far back to read. Capped, because the chart behind it is a fixed number
    /// of pixels and a year of hourly rows is a slow query rendered as a smear.
    /// </param>
    private static async Task<Ok<IReadOnlyList<MetricPointDto>>> GetMetricsAsync(
        Guid id,
        AirsideDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct,
        int hours = 24)
    {
        var window = Math.Clamp(hours, 1, 24 * 90);
        var since = timeProvider.GetUtcNow().UtcDateTime.AddHours(-window);

        var rows = await db.MetricRollups
            .AsNoTracking()
            .Where(r => r.WorkloadId == id && r.HourUtc >= since)
            .OrderBy(r => r.HourUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<MetricPointDto>>(
        [
            .. rows.Select(r => new MetricPointDto(
                new DateTimeOffset(r.HourUtc, TimeSpan.Zero),
                r.CpuNanosAvg,
                r.CpuNanosMax,
                r.MemoryBytesAvg,
                r.MemoryBytesMax,
                r.MemoryLimitBytes,
                r.NetworkRxBytes,
                r.NetworkTxBytes,
                r.SampleCount)),
        ]);
    }

    private static async Task<Ok<IReadOnlyList<NotificationDto>>> ListNotificationsAsync(
        AirsideDbContext db,
        CancellationToken ct,
        bool includeResolved = false)
    {
        var query = db.Notifications.AsNoTracking();

        if (!includeResolved)
        {
            query = query.Where(n => n.ResolvedAt == null);
        }

        var rows = await query
            .OrderByDescending(n => n.Severity)
            .ThenByDescending(n => n.LastSeenAt)
            .Take(200)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<NotificationDto>>(
        [
            .. rows.Select(n => new NotificationDto(
                n.Id,
                n.Severity.ToString().ToLowerInvariant(),
                n.Title,
                n.Body,
                n.Code,
                n.ResourceKind,
                n.ResourceId,
                n.OccurrenceCount,
                new DateTimeOffset(n.FirstSeenAt, TimeSpan.Zero),
                new DateTimeOffset(n.LastSeenAt, TimeSpan.Zero),
                n.AcknowledgedAt is not null,
                n.ResolvedAt is not null)),
        ]);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> AcknowledgeAsync(
        Guid id,
        AirsideDbContext db,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id, ct).ConfigureAwait(false);

        if (notification is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such notification.").ToProblem();
        }

        // Acknowledged, not resolved. The operator has seen it; whether the
        // underlying condition still holds is for the sweep that raised it to say.
        notification.AcknowledgedAt = timeProvider.GetUtcNow().UtcDateTime;
        notification.AcknowledgedByUserId =
            Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<UpdateRecordDto>>> ListUpdatesAsync(
        AirsideDbContext db,
        CancellationToken ct)
    {
        var rows = await db.UpdateRecords
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<UpdateRecordDto>>(
        [
            .. rows.Select(r => new UpdateRecordDto(
                r.Id,
                r.FromVersion,
                r.ToVersion,
                r.Status.ToString().ToLowerInvariant(),
                r.ErrorCode,
                r.ErrorMessage,
                r.PreUpdateBackupPath,
                new DateTimeOffset(r.StartedAt, TimeSpan.Zero),
                r.CompletedAt is null ? null : new DateTimeOffset(r.CompletedAt.Value, TimeSpan.Zero))),
        ]);
    }

    /// <summary>
    /// Starts an update, which will take the control plane offline briefly.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the update is prepared, not when it finishes — this
    /// process is the one about to be replaced, so it cannot report the outcome.
    /// The result is reconciled at the next startup from <c>state.json</c>.
    /// </remarks>
    private static async Task<Results<Accepted<UpdateRecordDto>, ProblemHttpResult>> StartUpdateAsync(
        StartUpdateRequest request,
        UpdateOrchestrator orchestrator,
        AirsideDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            return new Error(ErrorCodes.ValidationFailed, "A version is required.").ToProblem();
        }

        // Never ':latest'. An update path that resolves a moving tag cannot say
        // what it is updating to, cannot record what to roll back from, and turns
        // a routine restart into an unplanned upgrade.
        if (request.Version.Contains("latest", StringComparison.OrdinalIgnoreCase))
        {
            return new Error(
                "update.version_not_pinned",
                "Updates must name an explicit version. A moving tag makes it impossible to record what "
                + "was running or to roll back to it.").ToProblem();
        }

        var userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)
            ? uid
            : (Guid?)null;

        var result = await orchestrator.PrepareAsync(request.Version, userId, ct).ConfigureAwait(false);

        if (result == UpdateOrchestrator.Result.AlreadyCurrent)
        {
            return new Error(
                "update.already_current",
                $"This instance is already running {request.Version}.").ToProblem();
        }

        var record = await db.UpdateRecords
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .FirstAsync(ct)
            .ConfigureAwait(false);

        var dto = new UpdateRecordDto(
            record.Id,
            record.FromVersion,
            record.ToVersion,
            record.Status.ToString().ToLowerInvariant(),
            record.ErrorCode,
            record.ErrorMessage,
            record.PreUpdateBackupPath,
            new DateTimeOffset(record.StartedAt, TimeSpan.Zero),
            null);

        if (result == UpdateOrchestrator.Result.Failed)
        {
            return new Error(
                record.ErrorCode ?? "update.prepare_failed",
                record.ErrorMessage ?? "The update could not be prepared. Nothing was changed.").ToProblem();
        }

        return TypedResults.Accepted($"/api/v1/system/updates", dto);
    }

    private static async Task<Results<Ok<SystemBackupResult>, ProblemHttpResult>> CreateSystemBackupAsync(
        ISystemBackupProvider backups,
        UpdateOptions options,
        CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await backups.CreateAsync(options.BackupRoot, ct).ConfigureAwait(false));
        }
        catch (InvalidOperationException ex)
        {
            return new Error("backup.system_failed", ex.Message).ToProblem();
        }
    }
}
