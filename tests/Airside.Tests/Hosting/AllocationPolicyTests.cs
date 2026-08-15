using Airside.Core.Common;
using Airside.Core.Hosting;

namespace Airside.Tests.Hosting;

/// <summary>
/// The admission gate. Its failure mode is admitting a workload the host cannot
/// run, so the boundary cases are tested exactly rather than approximately.
/// </summary>
public class AllocationPolicyTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static readonly StrictNoOvercommitPolicy Policy = new();

    private static ResourcePosition Position(
        long capacityMemory = 8 * Gib,
        long reserveMemory = 1 * Gib,
        long allocatedMemory = 0,
        long capacityCpu = 4_000_000_000,
        long reserveCpu = 1_000_000_000,
        long allocatedCpu = 0,
        long capacityStorage = 100 * Gib,
        long reserveStorage = 10 * Gib,
        long allocatedStorage = 0) =>
        new(
            new HostCapacity(capacityCpu, capacityMemory, capacityStorage, DateTimeOffset.UnixEpoch),
            new HostReserve(reserveCpu, reserveMemory, reserveStorage),
            new ResourceTriple(allocatedCpu, allocatedMemory, allocatedStorage),
            Used: null,
            StorageEnforcement.Accounting);

    private static ResourceTriple Request(long memory = 0, long cpu = 0, long storage = 0) =>
        new(cpu, memory, storage);

    [Fact]
    public void Admit_ExactlyFillingAvailable_Succeeds()
    {
        // 8 GiB capacity, 1 GiB reserve, 5 GiB allocated leaves exactly 2 GiB.
        var result = Policy.Admit(
            Position(allocatedMemory: 5 * Gib),
            Request(memory: 2 * Gib));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Admit_OneByteOverAvailable_Fails()
    {
        var result = Policy.Admit(
            Position(allocatedMemory: 5 * Gib),
            Request(memory: (2 * Gib) + 1));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ResourceInsufficientMemory, result.Failure!.Code);
    }

    [Fact]
    public void Admit_WouldEatIntoReserve_Fails()
    {
        // 7 GiB free by naive arithmetic, but 1 GiB of it is the host's reserve.
        var result = Policy.Admit(Position(), Request(memory: 8 * Gib));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ResourceInsufficientMemory, result.Failure!.Code);
    }

    [Fact]
    public void Admit_Failure_CarriesEveryNumberTheClientNeeds()
    {
        var result = Policy.Admit(
            Position(allocatedMemory: 5 * Gib),
            Request(memory: 4 * Gib));

        var metadata = result.Failure!.Metadata!;

        // The UI renders these; it must never have to parse the message.
        Assert.Equal(4 * Gib, metadata["requested"]);
        Assert.Equal(2 * Gib, metadata["available"]);
        Assert.Equal(8 * Gib, metadata["capacity"]);
        Assert.Equal(5 * Gib, metadata["allocated"]);
        Assert.Equal(1 * Gib, metadata["reserved"]);
    }

    [Fact]
    public void Admit_UndiscoveredHost_AdmitsNothing()
    {
        // A freshly seeded host has zero capacity and a non-zero reserve, so the
        // subtraction goes negative. It must clamp to zero and reject, not wrap
        // into a large available figure and admit everything.
        var result = Policy.Admit(
            Position(capacityMemory: 0, capacityCpu: 0, capacityStorage: 0),
            Request(memory: 1));

        Assert.True(result.IsFailure);
        Assert.Equal(0L, result.Failure!.Metadata!["available"]);
    }

    [Fact]
    public void Admit_CpuExhausted_ReportsCpuNotMemory()
    {
        var result = Policy.Admit(
            Position(allocatedCpu: 3_000_000_000),
            Request(cpu: 1_000_000_000));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ResourceInsufficientCpu, result.Failure!.Code);
    }

    [Fact]
    public void Admit_StorageExhausted_ReportsStorage()
    {
        var result = Policy.Admit(
            Position(allocatedStorage: 89 * Gib),
            Request(storage: 2 * Gib));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ResourceInsufficientStorage, result.Failure!.Code);
    }

    [Fact]
    public void Admit_ZeroRequest_Succeeds()
    {
        Assert.True(Policy.Admit(Position(), Request()).IsSuccess);
    }
}
