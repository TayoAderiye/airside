using Airside.Core.Containers;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Operations;

/// <summary>
/// Samples container usage and folds it into hourly rows.
/// </summary>
/// <remarks>
/// <para>
/// Samples are folded as they arrive rather than stored and aggregated later.
/// Keeping raw samples would put millions of rows a month on the same disk as the
/// workloads, for a question nobody asks: what an operator wants to know is
/// whether something was busy, and an hourly min/avg/max answers that.
/// </para>
/// <para>
/// The running total lives in the database row, not in memory, so a restart
/// halfway through an hour continues the same average instead of starting a
/// second row that halves it.
/// </para>
/// </remarks>
public sealed class MetricSampler(
    IServiceScopeFactory scopeFactory,
    IContainerRuntime runtime,
    TimeProvider timeProvider,
    ILogger<MetricSampler> logger) : BackgroundService
{
    /// <summary>
    /// How often usage is read.
    /// </summary>
    /// <remarks>
    /// A minute. Docker's stats endpoint is not free — it holds a stream open per
    /// container — and at hourly resolution sixty samples is already far more than
    /// the average needs.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Rollups older than this are dropped.</summary>
    /// <remarks>
    /// Ninety days of hourly rows is about two thousand per workload, which is
    /// small enough to keep and long enough to cover "was this happening last
    /// quarter". Unbounded growth on a control-plane database is how a tool like
    /// this eventually fills the disk it is managing.
    /// </remarks>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);

        do
        {
            await SampleAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        try
        {
            var containers = await runtime.Containers.ListManagedAsync(null, ct).ConfigureAwait(false);

            var running = containers
                .Where(c => c.State == ContainerRunState.Running
                    && c.Labels.TryGetValue(Core.Naming.AirsideLabels.WorkloadId, out _))
                .ToList();

            if (running.Count == 0)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);

            foreach (var container in running)
            {
                if (!Guid.TryParse(container.Labels[Core.Naming.AirsideLabels.WorkloadId], out var workloadId))
                {
                    continue;
                }

                var sample = await runtime.Containers.SampleStatsAsync(container.Id, ct).ConfigureAwait(false);

                // Null on the first call for a container: CPU is a delta between
                // two readings, so there is nothing to report until the second.
                if (sample?.CpuNanos is null)
                {
                    continue;
                }

                await FoldAsync(db, workloadId, hour, sample, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await PruneAsync(db, now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Metrics must never take the process down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogDebug(ex, "A metric sample failed; the next tick will try again");
        }
    }

    /// <summary>
    /// Adds one sample to the hour's row, keeping the average correct.
    /// </summary>
    /// <remarks>
    /// The average is recomputed from the previous average and the sample count
    /// rather than from a stored sum. A sum of CPU percentages over a long-running
    /// hour is meaningless on its own and invites someone to read it as a total.
    /// </remarks>
    private static async Task FoldAsync(
        AirsideDbContext db,
        Guid workloadId,
        DateTime hour,
        ContainerStatsSample sample,
        CancellationToken ct)
    {
        var rollup = await db.MetricRollups
            .FirstOrDefaultAsync(r => r.WorkloadId == workloadId && r.HourUtc == hour, ct)
            .ConfigureAwait(false);

        var cpuNanos = sample.CpuNanos ?? 0;

        if (rollup is null)
        {
            db.MetricRollups.Add(new MetricRollup
            {
                Id = Guid.CreateVersion7(),
                WorkloadId = workloadId,
                HourUtc = hour,
                SampleCount = 1,
                CpuNanosAvg = cpuNanos,
                CpuNanosMax = cpuNanos,
                MemoryBytesAvg = sample.MemoryBytes,
                MemoryBytesMax = sample.MemoryBytes,
                MemoryLimitBytes = sample.MemoryLimitBytes,
                NetworkRxBytes = sample.NetworkRxBytes,
                NetworkTxBytes = sample.NetworkTxBytes,
            });

            return;
        }

        var count = rollup.SampleCount + 1;

        rollup.CpuNanosAvg = (long)(((double)rollup.CpuNanosAvg * rollup.SampleCount + cpuNanos) / count);
        rollup.CpuNanosMax = Math.Max(rollup.CpuNanosMax, cpuNanos);
        rollup.MemoryBytesAvg = (long)(((double)rollup.MemoryBytesAvg * rollup.SampleCount + sample.MemoryBytes) / count);
        rollup.MemoryBytesMax = Math.Max(rollup.MemoryBytesMax, sample.MemoryBytes);
        rollup.MemoryLimitBytes = sample.MemoryLimitBytes;

        // Counters, not deltas — the container reports cumulative totals, so the
        // latest reading is the hour's figure so far.
        rollup.NetworkRxBytes = sample.NetworkRxBytes;
        rollup.NetworkTxBytes = sample.NetworkTxBytes;
        rollup.SampleCount = count;
    }

    private static async Task PruneAsync(AirsideDbContext db, DateTime now, CancellationToken ct)
    {
        // Only on the hour, so the delete does not run sixty times an hour to
        // remove nothing.
        if (now.Minute >= 1)
        {
            return;
        }

        var cutoff = now.Subtract(Retention);

        await db.MetricRollups
            .Where(r => r.HourUtc < cutoff)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
