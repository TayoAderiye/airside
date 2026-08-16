using Airside.Core.Hosting;

namespace Airside.Tests.Hosting;

/// <summary>
/// That the host reserve leaves something behind on a small machine.
/// </summary>
/// <remarks>
/// <para>
/// It used to be a fixed one core, one GiB and ten GiB. A fixed reserve does not
/// scale down, and the failure is total rather than partial: on a one-core
/// instance the CPU reserve took the whole core, so the allocation gate refused
/// every workload with "Not enough cpu available. requested 500000000,
/// available 0" on an idle host.
/// </para>
/// <para>
/// The numbers below are from the instance that found it — a t3.small, 1 vCPU
/// and 2 GB, which is the size the README recommends as a starting point.
/// </para>
/// </remarks>
public class HostReserveTests
{
    private const long Core = 1_000_000_000L;
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>The capacity actually reported by a t3.small running Airside.</summary>
    private const long SmallCpu = 1_000_000_000L;

    private const long SmallMemory = 2_056_331_264L;
    private const long SmallStorage = 6_800_000_000L;

    [Fact]
    public void ASingleCoreHostKeepsMostOfItsCore()
    {
        var reserve = HostReserve.For(SmallCpu, SmallMemory, SmallStorage);

        Assert.True(
            reserve.CpuNanos < SmallCpu,
            "the reserve must not take the entire CPU, or nothing can ever be deployed");

        // Enough left for the half-core application that was refused.
        Assert.True(SmallCpu - reserve.CpuNanos >= 500_000_000L);
    }

    [Fact]
    public void ASingleCoreHostCanStillFitAGigabyteDatabase()
    {
        // The Redis that was refused asked for 1 GiB against 2 GB of capacity
        // with nothing else allocated.
        var reserve = HostReserve.For(SmallCpu, SmallMemory, SmallStorage);

        Assert.True(SmallMemory - reserve.MemoryBytes >= GiB);
    }

    [Fact]
    public void TheStorageReserveNeverExceedsTheDisk()
    {
        // The old fixed 10 GiB was larger than the whole filesystem on an 8 GiB
        // cloud image, so available storage came out at or below zero before any
        // workload asked for anything.
        var reserve = HostReserve.For(SmallCpu, SmallMemory, SmallStorage);

        Assert.True(reserve.StorageBytes < SmallStorage);
    }

    [Fact]
    public void ALargeHostDoesNotHaveAProportionallyLargeReserve()
    {
        // The control plane's footprint is roughly constant, so a plain
        // percentage would hold back 8 GiB on a 32 GiB box for no reason.
        var reserve = HostReserve.For(8 * Core, 32 * GiB, 500 * GiB);

        Assert.True(reserve.CpuNanos <= Core);
        Assert.True(reserve.MemoryBytes <= 2 * GiB);
        Assert.True(reserve.StorageBytes <= 20 * GiB);
    }

    [Fact]
    public void AHostSmallerThanTheFloorStillReportsSomethingAvailable()
    {
        // Absurdly small, but the arithmetic has to hold: reserving more than
        // capacity would make available negative and the gate report nonsense.
        var reserve = HostReserve.For(100_000_000L, 128L * 1024 * 1024, 1L * 1024 * 1024 * 1024);

        Assert.True(reserve.CpuNanos <= 100_000_000L);
        Assert.True(reserve.MemoryBytes <= 128L * 1024 * 1024);
        Assert.True(reserve.StorageBytes <= 1L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void UndiscoveredCapacityFallsBackToTheSmallEnd()
    {
        // A host row exists before discovery runs. Guessing high there would
        // refuse workloads on a machine that turns out to be large.
        Assert.Equal(HostReserve.Default.CpuNanos, HostReserve.For(0, 0, 0).CpuNanos);
        Assert.True(HostReserve.Default.CpuNanos < Core);
    }
}
