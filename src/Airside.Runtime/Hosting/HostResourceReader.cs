using System.Globalization;
using Airside.Core.Hosting;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Hosting;

/// <summary>
/// Reads real host capacity and usage.
/// </summary>
/// <remarks>
/// <para>
/// The API runs in a container, so <c>/proc</c> inside it describes the container,
/// not the machine. The host's <c>/proc</c> and <c>/sys</c> are bind-mounted
/// read-only at <see cref="ProcRoot"/> and <see cref="SysRoot"/> by the compose
/// file; without those mounts this reports the container's own limits, which on a
/// control plane with a 512 MB limit means a host that appears to have 512 MB.
/// The reader fails loudly rather than quietly reporting the wrong machine.
/// </para>
/// </remarks>
public sealed class HostResourceReader(ILogger<HostResourceReader> logger, TimeProvider timeProvider)
    : IHostResourceReader
{
    public const string ProcRoot = "/host/proc";
    public const string SysRoot = "/host/sys";

    public Task<HostCapacity> ReadCapacityAsync(string volumeRoot, CancellationToken ct)
    {
        var memoryBytes = ReadMemTotalBytes();
        var cpuCount = ReadCpuCount();
        var storageBytes = ReadFilesystemTotalBytes(volumeRoot);

        return Task.FromResult(new HostCapacity(
            CpuNanos: cpuCount * 1_000_000_000L,
            MemoryBytes: memoryBytes,
            StorageBytes: storageBytes,
            DiscoveredAt: timeProvider.GetUtcNow()));
    }

    public Task<ResourceTriple> ReadUsageAsync(string volumeRoot, CancellationToken ct)
    {
        var total = ReadMemTotalBytes();
        var available = ReadMemInfoValueKb("MemAvailable") * 1024;

        var used = total > 0 && available > 0 ? total - available : 0;
        var storageUsed = ReadFilesystemUsedBytes(volumeRoot);

        // CPU usage over an interval belongs to the metrics sampler, which holds a
        // previous reading. Reporting an instantaneous figure here would mean
        // inventing one.
        return Task.FromResult(new ResourceTriple(0, used, storageUsed));
    }

    /// <summary>
    /// Detects whether the volume root can enforce per-volume quotas.
    /// </summary>
    /// <remarks>
    /// Only XFS with the <c>prjquota</c> mount option can. Everything else —
    /// which is every default EC2 Ubuntu and Amazon Linux image — returns
    /// <see cref="StorageEnforcement.Accounting"/>, and the API surfaces that so
    /// the UI does not present storage allocation as a guarantee it cannot keep.
    /// </remarks>
    public Task<StorageEnforcement> DetectStorageEnforcementAsync(string volumeRoot, CancellationToken ct)
    {
        var mountsPath = Path.Combine(ProcRoot, "mounts");

        if (!File.Exists(mountsPath))
        {
            return Task.FromResult(StorageEnforcement.Accounting);
        }

        try
        {
            string? bestMatch = null;
            var bestLength = -1;

            foreach (var line in File.ReadLines(mountsPath))
            {
                var parts = line.Split(' ');

                if (parts.Length < 4)
                {
                    continue;
                }

                var mountPoint = parts[1];

                // The longest matching mount point is the filesystem the volume
                // root actually lives on; "/" matches everything and must lose.
                if (volumeRoot.StartsWith(mountPoint, StringComparison.Ordinal)
                    && mountPoint.Length > bestLength)
                {
                    bestLength = mountPoint.Length;
                    bestMatch = $"{parts[2]} {parts[3]}";
                }
            }

            var supportsQuota = bestMatch is not null
                && bestMatch.StartsWith("xfs ", StringComparison.Ordinal)
                && (bestMatch.Contains("prjquota", StringComparison.Ordinal)
                    || bestMatch.Contains("pquota", StringComparison.Ordinal));

            if (!supportsQuota)
            {
                logger.LogInformation(
                    "Storage quotas are unavailable on {VolumeRoot} ({Mount}); allocation is accounted, not enforced",
                    volumeRoot,
                    bestMatch ?? "unknown filesystem");
            }

            return Task.FromResult(supportsQuota ? StorageEnforcement.Quota : StorageEnforcement.Accounting);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read {MountsPath}; assuming storage accounting only", mountsPath);
            return Task.FromResult(StorageEnforcement.Accounting);
        }
    }

    private long ReadMemTotalBytes() => ReadMemInfoValueKb("MemTotal") * 1024;

    private long ReadMemInfoValueKb(string key)
    {
        var path = Path.Combine(ProcRoot, "meminfo");

        if (!File.Exists(path))
        {
            logger.LogError(
                "{Path} is missing. The host /proc must be bind-mounted read-only at {ProcRoot}, "
                + "or Airside reports the API container's limits as the host's capacity.",
                path,
                ProcRoot);
            return 0;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith(key, StringComparison.Ordinal))
            {
                continue;
            }

            var digits = line.Split(':', 2)[1].Replace("kB", string.Empty, StringComparison.Ordinal).Trim();

            if (long.TryParse(digits, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    private int ReadCpuCount()
    {
        var path = Path.Combine(ProcRoot, "cpuinfo");

        if (!File.Exists(path))
        {
            logger.LogError("{Path} is missing; falling back to the container's visible processor count", path);
            return Environment.ProcessorCount;
        }

        var count = File.ReadLines(path)
            .Count(l => l.StartsWith("processor", StringComparison.Ordinal));

        return count > 0 ? count : Environment.ProcessorCount;
    }

    private long ReadFilesystemTotalBytes(string path) => SafeDriveInfo(path)?.TotalSize ?? 0;

    private long ReadFilesystemUsedBytes(string path)
    {
        var drive = SafeDriveInfo(path);
        return drive is null ? 0 : drive.TotalSize - drive.AvailableFreeSpace;
    }

    private DriveInfo? SafeDriveInfo(string path)
    {
        try
        {
            return Directory.Exists(path) ? new DriveInfo(path) : null;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Could not read filesystem statistics for {Path}", path);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read filesystem statistics for {Path}", path);
            return null;
        }
    }
}
