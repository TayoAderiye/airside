using Airside.Core.Hosting;

namespace Airside.Data.Entities;

/// <summary>
/// The machine Airside manages. One seeded row for now.
/// </summary>
/// <remarks>
/// This entity exists from the first migration despite there being exactly one of
/// them, because retrofitting a host dimension later means rewriting every
/// allocation query and every uniqueness constraint. It costs one foreign key
/// now; it costs a rewrite later, which is the usual reason "multi-host later"
/// never happens.
/// </remarks>
public class Host : Entity
{
    public string Name { get; set; } = "local";

    public bool IsLocal { get; set; } = true;

    // Capacity is discovered, never configured. An EC2 instance can be resized
    // underneath you, and a control plane still admitting workloads against a
    // remembered 16 GB after a downgrade to 8 is worse than one admitting none.
    public long CapacityCpuNanos { get; set; }

    public long CapacityMemoryBytes { get; set; }

    public long CapacityStorageBytes { get; set; }

    public long ReserveCpuNanos { get; set; } = HostReserve.Default.CpuNanos;

    public long ReserveMemoryBytes { get; set; } = HostReserve.Default.MemoryBytes;

    public long ReserveStorageBytes { get; set; } = HostReserve.Default.StorageBytes;

    public StorageEnforcement StorageEnforcement { get; set; } = StorageEnforcement.Accounting;

    public string VolumeRoot { get; set; } = "/var/lib/airside/volumes";

    public string? DockerApiVersion { get; set; }

    public string? KernelVersion { get; set; }

    public string? OperatingSystem { get; set; }

    public DateTime? LastDiscoveredAt { get; set; }

    // There is deliberately no AllocatedX column. Allocated is derived from
    // workload rows on every admission check: a cached counter drifts, and the
    // admission gate is the worst possible place for drift.
}
