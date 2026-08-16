using System.Security.Cryptography;
using System.Text;
using Airside.Api.Contracts;
using Airside.Core.Containers;
using Airside.Core.Naming;

namespace Airside.Api.Features;

/// <summary>
/// Airside's own containers, listed alongside the workloads it manages.
/// </summary>
/// <remarks>
/// <para>
/// Both summary DTOs have carried an <c>IsSystem</c> flag from the start, the
/// compose file labels every one of these <c>airside.system=true</c>, and its
/// comment says they are visible in the UI. None of it was ever read: the flag
/// was hardcoded to false and the label went nowhere. An operator looking at a
/// working install saw an empty Applications list and an empty Databases list,
/// with four containers running.
/// </para>
/// <para>
/// These are not rows in any table. Compose creates them, so they have no
/// workload record, no deployment history and no lifecycle Airside owns — they
/// are discovered from Docker each time they are asked for. That is also what
/// makes them safe to show: their ids exist nowhere in the database, so every
/// action endpoint looks them up, finds nothing, and returns 404 without a
/// single guard having to be remembered. Stopping the API through the dashboard
/// that the API is serving is not reachable by accident.
/// </para>
/// </remarks>
internal sealed class SystemWorkloadReader(IContainerRuntime runtime)
{
    /// <summary>Everything that is not the control-plane store.</summary>
    private static readonly string[] ApplicationContainers =
    [
        AirsideLabels.SystemContainers.Api,
        AirsideLabels.SystemContainers.Ui,
        AirsideLabels.SystemContainers.Proxy,
    ];

    public async Task<IReadOnlyList<ApplicationSummaryDto>> ApplicationsAsync(CancellationToken ct)
    {
        var found = new List<ApplicationSummaryDto>();

        foreach (var name in ApplicationContainers)
        {
            var container = await runtime.Containers.FindAsync(name, ct).ConfigureAwait(false);

            if (container is null)
            {
                continue;
            }

            found.Add(new ApplicationSummaryDto(
                StableId(name),
                name,
                DisplayNameFor(name),
                StateOf(container),
                container.StartedAt ?? container.CreatedAt,
                "image",

                // Not zero because they are unlimited — because Airside did not
                // set them and does not know. Compose did, and reading a limit
                // back out of Docker to display it as an Airside allocation
                // would put them in the host allocation arithmetic, where they
                // do not belong.
                CpuNanos: 0,
                MemoryBytes: 0,
                ContainerPort: PortFor(name),
                CurrentDeploymentId: null,
                ActiveJobId: null,
                IsSystem: true));
        }

        return found;
    }

    public async Task<IReadOnlyList<DatabaseSummaryDto>> DatabasesAsync(CancellationToken ct)
    {
        var container = await runtime.Containers
            .FindAsync(AirsideLabels.SystemContainers.Database, ct)
            .ConfigureAwait(false);

        if (container is null)
        {
            // Absent under the SQLite store, where there is no database
            // container at all. Reporting one would be a lie about the install.
            return [];
        }

        var name = AirsideLabels.SystemContainers.Database;

        return
        [
            new DatabaseSummaryDto(
                StableId(name),
                name,
                "Airside store",
                "postgres",
                VersionOf(container.Image),
                StateOf(container),
                container.StartedAt ?? container.CreatedAt,
                CpuNanos: 0,
                MemoryBytes: 0,
                StorageBytes: 0,
                StorageUsedBytes: null,
                ActiveJobId: null,
                DriftState: "none",
                IsSystem: true),
        ];
    }

    /// <summary>
    /// Every container name this reader will ever surface.
    /// </summary>
    /// <remarks>
    /// The allowlist is what makes <see cref="ResolveContainerName"/> safe. It
    /// maps ids to names by trying these four and nothing else, so no id an
    /// attacker can construct resolves to a container of their choosing.
    /// </remarks>
    private static readonly string[] AllContainers =
    [
        AirsideLabels.SystemContainers.Api,
        AirsideLabels.SystemContainers.Ui,
        AirsideLabels.SystemContainers.Proxy,
        AirsideLabels.SystemContainers.Database,
    ];

    /// <summary>
    /// The container behind a synthesised id, or <c>null</c> if it is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading is not controlling. These ids match no row, and the original
    /// intent was that every endpoint would therefore 404 — which is right for
    /// stopping the API through the dashboard the API is serving, and wrong for
    /// looking at its log. The result was four containers listed in the UI that
    /// could not be clicked, inspected, or read, on the screen whose entire
    /// purpose is watching what the host is doing.
    /// </para>
    /// <para>
    /// So this exists, and only log streaming calls it. The lifecycle endpoints
    /// go on looking their ids up in the database and finding nothing, so
    /// nothing destructive became reachable by adding it.
    /// </para>
    /// </remarks>
    public static string? ResolveContainerName(Guid id) =>
        Array.Find(AllContainers, name => StableId(name) == id);

    /// <summary>
    /// The same id for the same container name, every time.
    /// </summary>
    /// <remarks>
    /// A random id per request would make React lists reorder and re-render on
    /// every poll. These ids match no row in any table, so the lifecycle
    /// endpoints answer 404 for them, which is the correct answer: there is
    /// nothing there to stop.
    /// </remarks>
    private static Guid StableId(string containerName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("airside.system:" + containerName));

        return new Guid(hash.AsSpan(0, 16));
    }

    private static string StateOf(ContainerSummary container) =>
        container.State switch
        {
            ContainerRunState.Running when container.Health == ContainerHealth.Unhealthy => "unhealthy",
            ContainerRunState.Running => "running",
            ContainerRunState.Restarting => "restarting",
            _ => "stopped",
        };

    /// <summary>The tag, which for Airside's own images is the version.</summary>
    private static string VersionOf(string image)
    {
        var colon = image.LastIndexOf(':');

        // A digest reference has no readable version, and a slash after the
        // colon means the colon was a registry port rather than a tag.
        return colon > 0 && !image[(colon + 1)..].Contains('/', StringComparison.Ordinal)
            ? image[(colon + 1)..]
            : "unknown";
    }

    private static string DisplayNameFor(string name) => name switch
    {
        AirsideLabels.SystemContainers.Api => "Airside API",
        AirsideLabels.SystemContainers.Ui => "Airside dashboard",
        AirsideLabels.SystemContainers.Proxy => "Airside proxy",
        _ => name,
    };

    private static int PortFor(string name) => name switch
    {
        AirsideLabels.SystemContainers.Api => 8080,
        AirsideLabels.SystemContainers.Ui => 3000,
        AirsideLabels.SystemContainers.Proxy => 80,
        _ => 0,
    };
}
