using Airside.Api.Contracts;
using Airside.Core.Containers;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Hubs;

/// <summary>
/// Live job progress and step logs.
/// </summary>
/// <remarks>
/// Subscribing replays the job's persisted steps before streaming new ones, so a
/// client that connects after a provision has started sees the whole story rather
/// than joining midway through.
/// </remarks>
[Authorize]
public sealed class JobsHub(AirsideDbContext db) : Hub
{
    public static string Group(Guid jobId) => $"job:{jobId}";

    public async Task Subscribe(Guid jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(jobId)).ConfigureAwait(false);

        var job = await db.Jobs
            .AsNoTracking()
            .Include(j => j.Steps)
            .FirstOrDefaultAsync(j => j.Id == jobId, Context.ConnectionAborted)
            .ConfigureAwait(false);

        if (job is null)
        {
            return;
        }

        await Clients.Caller.SendAsync("JobUpdated", JobDto.From(job), Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public Task Unsubscribe(Guid jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(jobId));
}

/// <summary>
/// Streams container logs.
/// </summary>
/// <remarks>
/// The stream is bounded on purpose. A container writing faster than a client
/// reads would otherwise grow an unbounded server-side buffer, so the reader
/// stops and reports backpressure rather than trading the control plane's memory
/// for one browser tab's log view.
/// </remarks>
[Authorize(Permissions.LogsRead)]
public sealed class LogsHub(IContainerRuntime runtime, ILogger<LogsHub> logger) : Hub
{
    private const int MaxLinesPerSecond = 2000;

    public async IAsyncEnumerable<LogLineDto> Stream(
        string containerId,
        int tailLines,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var query = new LogQuery { TailLines = tailLines, Follow = true };
        var windowStart = DateTimeOffset.UtcNow;
        var lineCount = 0;

        await foreach (var line in runtime.Containers.StreamLogsAsync(containerId, query, ct)
            .ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow - windowStart > TimeSpan.FromSeconds(1))
            {
                windowStart = DateTimeOffset.UtcNow;
                lineCount = 0;
            }

            if (++lineCount > MaxLinesPerSecond)
            {
                logger.LogWarning(
                    "Log stream for {ContainerId} exceeded {Limit} lines/second; dropping the subscription",
                    containerId, MaxLinesPerSecond);
                yield break;
            }

            yield return new LogLineDto(
                line.Timestamp,
                line.Stream == LogSource.StandardError ? "stderr" : "stdout",
                line.Text);
        }
    }
}

[Authorize(Permissions.MetricsRead)]
public sealed class MetricsHub : Hub
{
    public static string Group(Guid workloadId) => $"metrics:{workloadId}";

    public Task Subscribe(Guid workloadId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, Group(workloadId));
}

[Authorize]
public sealed class NotificationsHub : Hub;

/// <summary>Pushes job changes to subscribed clients.</summary>
public sealed class JobProgressBroadcaster(IHubContext<JobsHub> hub, IServiceScopeFactory scopeFactory)
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

        if (job is not null)
        {
            await hub.Clients.Group(JobsHub.Group(jobId))
                .SendAsync("JobUpdated", JobDto.From(job), ct)
                .ConfigureAwait(false);
        }
    }

    public Task OnStepAppendedAsync(Guid jobId, int sequence, string name, string? message, CancellationToken ct) =>
        hub.Clients.Group(JobsHub.Group(jobId))
            .SendAsync("JobStepAppended", new JobStepDto(sequence, name, message, DateTimeOffset.UtcNow), ct);
}
