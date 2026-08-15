using Airside.Core.Common;

namespace Airside.Core.Hosting;

/// <summary>What the host physically has. Re-read on a timer, never configured.</summary>
/// <remarks>
/// An EC2 instance can be resized underneath you. A control plane that keeps
/// admitting workloads against a remembered 16 GB after a downgrade to 8 GB is
/// worse than one that admits none.
/// </remarks>
public sealed record HostCapacity(
    long CpuNanos,
    long MemoryBytes,
    long StorageBytes,
    DateTimeOffset DiscoveredAt);

/// <summary>Headroom kept for the host OS and the control plane itself.</summary>
public sealed record HostReserve(long CpuNanos, long MemoryBytes, long StorageBytes)
{
    public static HostReserve Default { get; } = new(
        CpuNanos: 1_000_000_000L,
        MemoryBytes: 1L * 1024 * 1024 * 1024,
        StorageBytes: 10L * 1024 * 1024 * 1024);
}

/// <summary>
/// The three numbers, kept distinct.
/// </summary>
/// <param name="Allocated">
/// The sum of configured limits across managed workloads. Always derived, never
/// stored — a cached counter drifts, and the admission gate is the worst place
/// for drift.
/// </param>
/// <param name="Used">Current actual consumption. Null when not yet sampled.</param>
public sealed record ResourcePosition(
    HostCapacity Capacity,
    HostReserve Reserve,
    ResourceTriple Allocated,
    ResourceTriple? Used,
    StorageEnforcement StorageEnforcement);

public sealed record ResourceTriple(long CpuNanos, long MemoryBytes, long StorageBytes)
{
    public static ResourceTriple Zero { get; } = new(0, 0, 0);

    public static ResourceTriple operator +(ResourceTriple left, ResourceTriple right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new ResourceTriple(
            left.CpuNanos + right.CpuNanos,
            left.MemoryBytes + right.MemoryBytes,
            left.StorageBytes + right.StorageBytes);
    }

    public static ResourceTriple Add(ResourceTriple left, ResourceTriple right) => left + right;
}

/// <summary>
/// Whether storage limits are actually enforced on this host.
/// </summary>
/// <remarks>
/// Docker's <c>local</c> volume driver over ext4, or xfs without <c>pquota</c> —
/// which is every default EC2 image — supports no per-volume size limit at all.
/// The API surfaces this value so the UI can say so plainly rather than implying
/// a guarantee that does not exist. See ARCHITECTURE.md §5.
/// </remarks>
public enum StorageEnforcement
{
    /// <summary>Counted for admission and alerted on, but not enforced by the kernel.</summary>
    Accounting,

    /// <summary>Enforced by XFS project quotas.</summary>
    Quota,
}

/// <summary>Reads real capacity and usage from the host.</summary>
public interface IHostResourceReader
{
    Task<HostCapacity> ReadCapacityAsync(string volumeRoot, CancellationToken ct);

    Task<ResourceTriple> ReadUsageAsync(string volumeRoot, CancellationToken ct);

    /// <summary>Detects whether the volume root's filesystem can enforce quotas.</summary>
    Task<StorageEnforcement> DetectStorageEnforcementAsync(string volumeRoot, CancellationToken ct);
}

/// <summary>
/// Decides whether a request fits.
/// </summary>
/// <remarks>
/// The MVP has exactly one implementation, which rejects any overcommit. It is an
/// interface so a ratio-based policy becomes a registration change rather than a
/// rewrite of the admission path.
/// </remarks>
public interface IAllocationPolicy
{
    /// <summary>
    /// Returns success, or a failure carrying <c>requested</c>, <c>available</c>,
    /// <c>capacity</c>, <c>allocated</c>, and <c>reserved</c> in its metadata so
    /// the client can render the numbers without parsing a message.
    /// </summary>
    Result Admit(ResourcePosition position, ResourceTriple requested);
}
