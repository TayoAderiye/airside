using Airside.Core.Hosting;
using Airside.Data;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Hosting;

public interface IHostAllocationReader
{
    Task<ResourcePosition> ReadPositionAsync(CancellationToken ct);
}

/// <summary>
/// Assembles capacity, allocation, and usage into one position.
/// </summary>
/// <remarks>
/// Allocated is computed here on every call rather than stored. A cached counter
/// drifts from reality the first time a workload row changes outside the path
/// that maintains it, and the admission gate is the worst possible place for
/// drift — the failure mode is admitting a workload the host cannot run.
/// </remarks>
public sealed class HostAllocationReader(
    AirsideDbContext db,
    IHostResourceReader reader) : IHostAllocationReader
{
    public async Task<ResourcePosition> ReadPositionAsync(CancellationToken ct)
    {
        var host = await db.Hosts.AsNoTracking().FirstAsync(ct).ConfigureAwait(false);

        var capacity = new HostCapacity(
            host.CapacityCpuNanos,
            host.CapacityMemoryBytes,
            host.CapacityStorageBytes,
            host.LastDiscoveredAt is null ? default : new DateTimeOffset(host.LastDiscoveredAt.Value, TimeSpan.Zero));

        var reserve = new HostReserve(
            host.ReserveCpuNanos,
            host.ReserveMemoryBytes,
            host.ReserveStorageBytes);

        // Derived on every call, never stored. A cached counter drifts the first
        // time a workload row changes outside the path that maintains it, and the
        // admission gate is the worst possible place for drift.
        //
        // Orphaned volumes are counted deliberately: their disk is still occupied,
        // and leaving them out would let a few delete-and-recreate cycles quietly
        // consume the host with nothing in the UI explaining where it went.
        var workloads = await db.Workloads
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Cpu = g.Sum(w => w.CpuLimitNanos),
                Memory = g.Sum(w => w.MemoryLimitBytes),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var volumes = await db.Volumes
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Allocated = g.Sum(v => v.SizeAllocationBytes),
                Measured = g.Sum(v => v.LastMeasuredBytes ?? 0L),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var allocated = new ResourceTriple(
            workloads?.Cpu ?? 0,
            workloads?.Memory ?? 0,
            volumes?.Allocated ?? 0);

        ResourceTriple? used = null;

        try
        {
            used = await reader.ReadUsageAsync(host.VolumeRoot, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Usage is a nice-to-have; the position must still render. Null means
            // "not sampled", which the UI shows as an em dash rather than zero.
        }

        return new ResourcePosition(
            capacity,
            reserve with { StorageBytes = StorageReserve(reserve, used, volumes?.Measured ?? 0) },
            allocated,
            used,
            host.StorageEnforcement);
    }

    /// <summary>
    /// The storage reserve, widened by disk Airside did not hand out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capacity is the whole filesystem, and the admission gate subtracts only
    /// the reserve and Airside's own allocations from it. Nothing accounted for
    /// the operating system and the container images, which on a fresh 8 GiB
    /// cloud instance are already 3.7 GiB. The gate believed it had 4.7 GiB to
    /// give out on a disk with 3.0 GiB free, and would have admitted a volume
    /// that could not fit.
    /// </para>
    /// <para>
    /// Airside's own volumes are subtracted back out, because their bytes appear
    /// in both numbers — once as filesystem usage and once as an allocation. A
    /// 20 GiB database volume that is 80% full would otherwise be counted twice
    /// and cost 16 GiB of apparent headroom.
    /// </para>
    /// <para>
    /// Unmeasured volumes count as empty, which errs toward reserving less. That
    /// is the wrong direction for safety, but the alternative — assuming a new
    /// volume is full — would refuse everything immediately after provisioning
    /// one. The measurement runs on a timer and converges.
    /// </para>
    /// </remarks>
    private static long StorageReserve(HostReserve reserve, ResourceTriple? used, long airsideMeasuredBytes)
    {
        if (used is null)
        {
            // Not sampled yet. The plain reserve is the honest answer rather
            // than a guess in either direction.
            return reserve.StorageBytes;
        }

        var foreignUsage = Math.Max(0, used.StorageBytes - airsideMeasuredBytes);

        return reserve.StorageBytes + foreignUsage;
    }
}

/// <summary>
/// Re-reads host capacity at startup and on a timer.
/// </summary>
/// <remarks>
/// Capacity is discovered, never configured. An EC2 instance can be resized
/// underneath you, and a control plane that keeps admitting workloads against a
/// remembered 16 GB after a downgrade to 8 GB is worse than one that admits none.
/// </remarks>
public sealed class HostDiscoveryService(
    IServiceScopeFactory scopeFactory,
    IHostResourceReader reader,
    Airside.Core.Containers.IContainerRuntime runtime,
    TimeProvider timeProvider,
    ILogger<HostDiscoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);

        do
        {
            await DiscoverAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task DiscoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

            var host = await db.Hosts.FirstAsync(ct).ConfigureAwait(false);
            var capacity = await reader.ReadCapacityAsync(host.VolumeRoot, ct).ConfigureAwait(false);

            host.CapacityCpuNanos = capacity.CpuNanos;
            host.CapacityMemoryBytes = capacity.MemoryBytes;
            host.CapacityStorageBytes = capacity.StorageBytes;

            // Recomputed whenever capacity is, because the reserve is a share of
            // it. The row is created before any discovery has run, so without
            // this it keeps the conservative starting value forever — and an
            // instance resized underneath Airside would keep the reserve it had
            // when it was smaller.
            var reserve = HostReserve.For(capacity.CpuNanos, capacity.MemoryBytes, capacity.StorageBytes);

            host.ReserveCpuNanos = reserve.CpuNanos;
            host.ReserveMemoryBytes = reserve.MemoryBytes;
            host.ReserveStorageBytes = reserve.StorageBytes;
            host.StorageEnforcement = await reader
                .DetectStorageEnforcementAsync(host.VolumeRoot, ct)
                .ConfigureAwait(false);

            if (await runtime.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                var info = await runtime.GetInfoAsync(ct).ConfigureAwait(false);
                host.DockerApiVersion = info.ApiVersion;
                host.KernelVersion = info.KernelVersion;
                host.OperatingSystem = info.OperatingSystem;
            }

            host.LastDiscoveredAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "Host capacity discovered: {CpuNanos} nanoCPU, {MemoryBytes} bytes memory, "
                + "{StorageBytes} bytes storage, storage enforcement {Enforcement}",
                capacity.CpuNanos, capacity.MemoryBytes, capacity.StorageBytes, host.StorageEnforcement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Discovery must never take the process down; stale capacity is survivable.
        catch (Exception ex)
        {
            logger.LogError(ex, "Host capacity discovery failed; the previous figures remain in force");
        }
#pragma warning restore CA1031
    }
}
