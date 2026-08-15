using Airside.Core.Common;

namespace Airside.Core.Hosting;

/// <summary>
/// Admits a request only if it fits inside capacity minus reserve. No overcommit.
/// </summary>
/// <remarks>
/// Pure arithmetic, so it lives in Core and is tested without a host. It is an
/// implementation of <see cref="IAllocationPolicy"/> rather than a static helper
/// so that a ratio-based overcommit policy is a registration change later, not a
/// rewrite of the admission path.
/// </remarks>
public sealed class StrictNoOvercommitPolicy : IAllocationPolicy
{
    public Result Admit(ResourcePosition position, ResourceTriple requested)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(requested);

        var memory = Check(
            ErrorCodes.ResourceInsufficientMemory,
            "memory",
            requested.MemoryBytes,
            position.Capacity.MemoryBytes,
            position.Reserve.MemoryBytes,
            position.Allocated.MemoryBytes);

        if (memory.IsFailure)
        {
            return memory;
        }

        var cpu = Check(
            ErrorCodes.ResourceInsufficientCpu,
            "cpu",
            requested.CpuNanos,
            position.Capacity.CpuNanos,
            position.Reserve.CpuNanos,
            position.Allocated.CpuNanos);

        if (cpu.IsFailure)
        {
            return cpu;
        }

        return Check(
            ErrorCodes.ResourceInsufficientStorage,
            "storage",
            requested.StorageBytes,
            position.Capacity.StorageBytes,
            position.Reserve.StorageBytes,
            position.Allocated.StorageBytes);
    }

    private static Result Check(
        string code,
        string dimension,
        long requested,
        long capacity,
        long reserve,
        long allocated)
    {
        // Clamped at zero: a host whose reserve exceeds its discovered capacity —
        // which is what a freshly seeded, not-yet-discovered host looks like —
        // must admit nothing rather than wrap into a large available figure.
        var available = Math.Max(0, capacity - reserve - allocated);

        if (requested <= available)
        {
            return Result.Ok();
        }

        return new Error(
            code,
            $"Not enough {dimension} available.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dimension"] = dimension,
                ["requested"] = requested,
                ["available"] = available,
                ["capacity"] = capacity,
                ["allocated"] = allocated,
                ["reserved"] = reserve,
            });
    }
}
