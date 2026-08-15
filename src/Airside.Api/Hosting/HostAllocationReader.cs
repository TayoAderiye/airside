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

        var storage = await db.Volumes
            .AsNoTracking()
            .SumAsync(v => v.SizeAllocationBytes, ct)
            .ConfigureAwait(false);

        var allocated = new ResourceTriple(workloads?.Cpu ?? 0, workloads?.Memory ?? 0, storage);

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

        return new ResourcePosition(capacity, reserve, allocated, used, host.StorageEnforcement);
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
