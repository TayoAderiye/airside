using Airside.Api.Contracts;
using Airside.Api.Realtime;
using Airside.Api.Security;
using Airside.Core.Containers;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features;

/// <summary>
/// The live streams, as ordinary HTTP endpoints.
/// </summary>
/// <remarks>
/// Per-resource rather than one multiplexed socket: the permission check is then
/// the resource's own, each stream is independently observable with
/// <c>curl -N</c>, and every route appears in the OpenAPI document like anything
/// else. A hub could do none of those.
/// </remarks>
internal static class RealtimeEndpoints
{
    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/jobs/{id:guid}/events", StreamJobAsync)
            .WithTags("Realtime")
            .RequireAuthorization();

        app.MapGet("/api/v1/databases/{id:guid}/logs/stream", StreamLogsAsync)
            .WithTags("Realtime")
            .RequirePermission(Permissions.LogsRead);

        app.MapGet("/api/v1/databases/{id:guid}/metrics/stream", StreamMetricsAsync)
            .WithTags("Realtime")
            .RequirePermission(Permissions.MetricsRead);

        app.MapGet("/api/v1/notifications/stream", StreamNotificationsAsync)
            .WithTags("Realtime")
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Job progress and the step log.
    /// </summary>
    /// <remarks>
    /// The ordering here is the interesting part. Subscribing happens
    /// <em>before</em> the persisted steps are read, so a step written during the
    /// replay query lands in the channel rather than falling into the gap between
    /// "what the database had" and "what the stream carries". Replayed sequences
    /// are then filtered out of the live feed, which is cheaper and more obviously
    /// correct than trying to make the two reads atomic.
    /// </remarks>
    private static async Task StreamJobAsync(
        Guid id,
        HttpContext http,
        AirsideDbContext db,
        IEventBus events,
        CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct).ConfigureAwait(false);

        if (job is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var resumeFrom = StreamCursor.ToSequence(ServerSentEventWriter.ResumeFrom(http.Request)) ?? -1;

        // Registered before the replay query runs, so a step written while the
        // replay is in flight lands in the channel instead of falling into the gap
        // between what the database had and what the stream carries.
        using var live = events.Subscribe(InProcessEventBus.JobTopic(id));

        await using var sse = await ServerSentEventWriter.StartAsync(http, ct).ConfigureAwait(false);

        var replayed = await db.JobSteps
            .AsNoTracking()
            .Where(s => s.JobId == id && s.Sequence > resumeFrom)
            .OrderBy(s => s.Sequence)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var highestSent = resumeFrom;

        foreach (var step in replayed)
        {
            await sse.SendAsync(
                new ServerSentEvent(
                    "job.step",
                    new JobStepDto(step.Sequence, step.Name, step.Message,
                        new DateTimeOffset(step.OccurredAt, TimeSpan.Zero)),
                    StreamCursor.FromSequence(step.Sequence)),
                ct).ConfigureAwait(false);

            highestSent = step.Sequence;
        }

        var current = await db.Jobs
            .AsNoTracking()
            .Include(j => j.Steps)
            .FirstAsync(j => j.Id == id, ct)
            .ConfigureAwait(false);

        await sse.SendAsync(new ServerSentEvent("job.updated", JobDto.From(current)), ct).ConfigureAwait(false);

        if (current.IsTerminal)
        {
            // Nothing further will ever arrive. Closing tells the browser this is
            // finished rather than leaving it to reconnect forever against a job
            // that ended an hour ago.
            await sse.SendClosingAsync("job-complete", ct).ConfigureAwait(false);
            return;
        }

        await foreach (var message in live.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (message.Payload is JobStepDto step)
            {
                if (step.Sequence <= highestSent)
                {
                    continue;
                }

                highestSent = step.Sequence;
            }

            await sse.SendAsync(message, ct).ConfigureAwait(false);

            if (string.Equals(message.EventName, "job.completed", StringComparison.Ordinal))
            {
                await sse.SendClosingAsync("job-complete", ct).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// Container logs.
    /// </summary>
    /// <remarks>
    /// Resume is the log timestamp, so a reconnect asks Docker for everything
    /// since the last line the client saw rather than replaying the tail and
    /// duplicating it.
    /// </remarks>
    private static async Task StreamLogsAsync(
        Guid id,
        HttpContext http,
        AirsideDbContext db,
        IContainerRuntime runtime,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        int tail = 200)
    {
        var containerId = await db.Databases
            .AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => d.ContainerId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (containerId is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var since = StreamCursor.ToTimestamp(ServerSentEventWriter.ResumeFrom(http.Request));

        var query = new LogQuery
        {
            TailLines = since is null ? Math.Clamp(tail, 1, 5000) : (int?)null,
            Since = since,
            Follow = true,
        };

        var logger = loggerFactory.CreateLogger("Airside.Api.Realtime.Logs");
        await using var sse = await ServerSentEventWriter.StartAsync(http, ct).ConfigureAwait(false);

        var windowStart = DateTimeOffset.UtcNow;
        var inWindow = 0;

        await foreach (var line in runtime.Containers.StreamLogsAsync(containerId, query, ct).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow - windowStart > TimeSpan.FromSeconds(1))
            {
                windowStart = DateTimeOffset.UtcNow;
                inWindow = 0;
            }

            if (++inWindow > MaxLinesPerSecond)
            {
                // A container writing faster than the client reads would otherwise
                // grow an unbounded buffer. Closing with a reason is better than
                // dropping lines silently: the client reconnects with
                // Last-Event-ID and continues from where it actually got to.
                logger.LogWarning(
                    "Log stream for {ContainerId} exceeded {Limit} lines/second; closing for resume",
                    containerId, MaxLinesPerSecond);

                await sse.SendClosingAsync("rate-limited", ct).ConfigureAwait(false);
                return;
            }

            await sse.SendAsync(
                new ServerSentEvent(
                    "log.line",
                    new LogLineDto(
                        line.Timestamp,
                        line.Stream == LogSource.StandardError ? "stderr" : "stdout",
                        line.Text),
                    StreamCursor.FromTimestamp(line.Timestamp)),
                ct).ConfigureAwait(false);
        }
    }

    private const int MaxLinesPerSecond = 2000;

    /// <summary>
    /// Live resource usage, sampled per connection.
    /// </summary>
    /// <remarks>
    /// Not persisted. Historical data is the hourly rollup; this is the live view,
    /// and storing every sample to serve a chart nobody is looking at is how a
    /// control plane ends up operating a time-series database it never wanted.
    /// </remarks>
    private static async Task StreamMetricsAsync(
        Guid id,
        HttpContext http,
        AirsideDbContext db,
        IContainerRuntime runtime,
        CancellationToken ct,
        int intervalSeconds = 5)
    {
        var containerId = await db.Databases
            .AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => d.ContainerId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (containerId is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using var sse = await ServerSentEventWriter.StartAsync(http, ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1, 60)));

        do
        {
            var sample = await runtime.Containers.SampleStatsAsync(containerId, ct).ConfigureAwait(false);

            if (sample is null)
            {
                await sse.SendClosingAsync("container-gone", ct).ConfigureAwait(false);
                return;
            }

            await sse.SendAsync(
                new ServerSentEvent(
                    "metric.sample",
                    new MetricSampleDto(
                        sample.SampledAt,
                        // Null on the first sample of a container: Docker's
                        // one-shot stats call has no previous CPU reading, and a
                        // plausible 0% would be a lie.
                        sample.CpuNanos,
                        sample.MemoryBytes,
                        sample.MemoryLimitBytes),
                    StreamCursor.FromTimestamp(sample.SampledAt)),
                ct).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private static async Task StreamNotificationsAsync(
        HttpContext http,
        IEventBus events,
        CancellationToken ct)
    {
        using var live = events.Subscribe(InProcessEventBus.NotificationsTopic);

        await using var sse = await ServerSentEventWriter.StartAsync(http, ct).ConfigureAwait(false);

        await foreach (var message in live.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await sse.SendAsync(message, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Publishes job progress onto the event stream.</summary>
public sealed class JobEventPublisher(IEventBus events, IServiceScopeFactory scopeFactory)
    : IJobProgressObserver
{
    public async Task OnJobUpdatedAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

        var job = await db.Jobs
            .AsNoTracking()
            .Include(j => j.Steps)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            .ConfigureAwait(false);

        if (job is null)
        {
            return;
        }

        await events.PublishAsync(
            InProcessEventBus.JobTopic(jobId),
            new ServerSentEvent(job.IsTerminal ? "job.completed" : "job.updated", JobDto.From(job)),
            ct).ConfigureAwait(false);
    }

    public Task OnStepAppendedAsync(Guid jobId, int sequence, string name, string? message, CancellationToken ct) =>
        events.PublishAsync(
            InProcessEventBus.JobTopic(jobId),
            new ServerSentEvent(
                "job.step",
                new JobStepDto(sequence, name, message, DateTimeOffset.UtcNow),
                StreamCursor.FromSequence(sequence)),
            ct).AsTask();
}
