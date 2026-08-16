using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Jobs;
using Airside.Core.Naming;
using Airside.Runtime.Jobs;
using Airside.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airside.Tests.Databases;

/// <summary>
/// "A deployment that fails at the health-check step must not leave an orphaned
/// container, volume, network, or proxy route behind."
/// </summary>
/// <remarks>
/// This is the test that makes that sentence true rather than aspirational, and
/// the retry case below is the one that distinguishes a failed provision from a
/// destroyed database.
/// </remarks>
public class ProvisionCompensationTests
{
    private static (DatabaseProvisionHandler Handler, FakeContainerRuntime Runtime, FakeJobContext Context,
        FakeDatabaseWorkloadStore Store) Build(ContainerHealth healthOnStart)
    {
        var runtime = new FakeContainerRuntime { HealthOnStart = healthOnStart };
        var store = new FakeDatabaseWorkloadStore(Snapshot());
        var registry = new FakeEngineRegistry();

        var handler = new DatabaseProvisionHandler(
            runtime, registry, store, new NoRegistryCredentials(),
            NullLogger<DatabaseProvisionHandler>.Instance);

        return (handler, runtime, new FakeJobContext(new DatabaseProvisionPayload(store.Snapshot.Id)), store);
    }

    private static DatabaseWorkloadSnapshot Snapshot()
    {
        Slug.TryCreate("orders", out var slug);
        var spec = Spec.Postgres() with { Slug = slug };

        return new DatabaseWorkloadSnapshot(
            spec.WorkloadId, slug, "Orders", DatabaseEngineKind.Postgres, spec,
            ContainerId: null,
            AirsideNames.Volume(slug, "data"),
            AirsideNames.DatabaseNetwork(slug),
            AirsideNames.DatabaseContainer(slug));
    }

    [Fact]
    public async Task Provision_WhenHealthy_CreatesEverythingAndRecordsRunning()
    {
        var (handler, runtime, context, store) = Build(ContainerHealth.Healthy);

        var result = await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(runtime.ContainerStore);
        Assert.Single(runtime.VolumeStore);
        Assert.Single(runtime.NetworkStore);
        Assert.Equal("Running", store.State);
    }

    [Fact]
    public async Task Provision_WhenHealthCheckFails_ReportsFailureWithoutSucceeding()
    {
        var (handler, _, context, _) = Build(ContainerHealth.Unhealthy);

        var result = await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ApplicationHealthCheckFailed, result.Failure!.Code);
    }

    [Fact]
    public async Task Compensate_AfterFailedHealthCheck_LeavesNoContainerVolumeOrNetwork()
    {
        var (handler, runtime, context, store) = Build(ContainerHealth.Unhealthy);

        await handler.ExecuteAsync(context, CancellationToken.None);
        await handler.CompensateAsync(context, CancellationToken.None);

        Assert.Empty(runtime.ContainerStore);
        Assert.Empty(runtime.VolumeStore);
        Assert.Empty(runtime.NetworkStore);
        Assert.Equal("Failed", store.State);
    }

    [Fact]
    public async Task Compensate_RemovesResourcesInReverseOrder()
    {
        // The container has to go before its network: Docker refuses to remove a
        // network that still has an endpoint attached.
        var (handler, runtime, context, _) = Build(ContainerHealth.Unhealthy);

        await handler.ExecuteAsync(context, CancellationToken.None);
        runtime.Operations.Clear();
        await handler.CompensateAsync(context, CancellationToken.None);

        var removals = runtime.Operations.Where(o => o.Contains(".remove", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, removals.Count);
        Assert.StartsWith("container.remove", removals[0], StringComparison.Ordinal);
        Assert.StartsWith("volume.remove", removals[1], StringComparison.Ordinal);
        Assert.StartsWith("network.remove", removals[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compensate_LeavesAPreExistingVolumeAlone()
    {
        // The single most consequential branch in the compensation path. A retry
        // over a volume that already held data must never delete it — that is the
        // difference between a failed provision and a destroyed database.
        var (handler, runtime, context, _) = Build(ContainerHealth.Unhealthy);

        Slug.TryCreate("orders", out var slug);
        var volumeName = AirsideNames.Volume(slug, "data");
        runtime.VolumeStore.Add(volumeName);

        await handler.ExecuteAsync(context, CancellationToken.None);
        await handler.CompensateAsync(context, CancellationToken.None);

        Assert.Contains(volumeName, runtime.VolumeStore, StringComparer.Ordinal);
        Assert.Empty(runtime.ContainerStore);
    }

    [Fact]
    public async Task Provision_RunTwice_CreatesOnlyOneContainer()
    {
        // Handlers must be idempotent by workload id: the startup recovery sweep
        // re-runs jobs that a dead process left behind.
        var (handler, runtime, context, _) = Build(ContainerHealth.Healthy);

        await handler.ExecuteAsync(context, CancellationToken.None);
        await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.Single(runtime.ContainerStore);
        Assert.Single(runtime.VolumeStore);
        Assert.Single(runtime.NetworkStore);
    }

    [Fact]
    public async Task Provision_TracksEveryResourceItCreates()
    {
        var (handler, _, context, _) = Build(ContainerHealth.Healthy);

        await handler.ExecuteAsync(context, CancellationToken.None);

        var tracked = await context.GetTrackedResourcesAsync(CancellationToken.None);

        Assert.Contains(tracked, r => r.Kind == JobResourceKind.Network && r.CreatedByThisJob);
        Assert.Contains(tracked, r => r.Kind == JobResourceKind.Volume && r.CreatedByThisJob);
        Assert.Contains(tracked, r => r.Kind == JobResourceKind.Container && r.CreatedByThisJob);
    }

    [Fact]
    public async Task Provision_WithPreExistingVolume_TracksItAsNotOurs()
    {
        var (handler, runtime, context, _) = Build(ContainerHealth.Healthy);

        Slug.TryCreate("orders", out var slug);
        runtime.VolumeStore.Add(AirsideNames.Volume(slug, "data"));

        await handler.ExecuteAsync(context, CancellationToken.None);

        var tracked = await context.GetTrackedResourcesAsync(CancellationToken.None);
        var volume = tracked.Single(r => r.Kind == JobResourceKind.Volume);

        Assert.False(volume.CreatedByThisJob);
    }
}

/// <summary>A job context that records progress, steps, and tracked resources in memory.</summary>
/// <summary>No stored credentials, which is the normal case for a public image.</summary>
public sealed class NoRegistryCredentials : IRegistryCredentialSource
{
    public Task<RegistryAuth?> ResolveAsync(ImageReference image, CancellationToken ct) =>
        Task.FromResult<RegistryAuth?>(null);
}

public sealed class FakeJobContext(object payload) : IJobContext
{
    private readonly List<TrackedResource> _tracked = [];

    public Guid JobId { get; } = Guid.CreateVersion7();

    public Guid? WorkloadId => null;

    public Guid? TriggeredByUserId => null;

    public List<(int Percent, string Step)> Progress { get; } = [];

    public List<(string Name, string Message)> Steps { get; } = [];

    public TPayload GetPayload<TPayload>() => (TPayload)payload;

    public Task ReportProgressAsync(int percent, string currentStep, CancellationToken ct)
    {
        Progress.Add((percent, currentStep));
        return Task.CompletedTask;
    }

    public Task LogStepAsync(string name, string message, CancellationToken ct)
    {
        Steps.Add((name, message));
        return Task.CompletedTask;
    }

    public Task TrackResourceAsync(JobResourceKind kind, string reference, bool createdByThisJob, CancellationToken ct)
    {
        if (!_tracked.Exists(r => r.Kind == kind && string.Equals(r.Reference, reference, StringComparison.Ordinal)))
        {
            _tracked.Add(new TrackedResource(kind, reference, createdByThisJob, DateTimeOffset.UnixEpoch));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TrackedResource>> GetTrackedResourcesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TrackedResource>>(_tracked);
}

public sealed class FakeDatabaseWorkloadStore(DatabaseWorkloadSnapshot snapshot) : IDatabaseWorkloadStore
{
    public DatabaseWorkloadSnapshot Snapshot { get; private set; } = snapshot;

    public string State { get; private set; } = "Provisioning";

    public bool VolumesRemoved { get; private set; }

    public bool Deleted { get; private set; }

    public Task<DatabaseWorkloadSnapshot?> GetAsync(Guid workloadId, CancellationToken ct) =>
        Task.FromResult<DatabaseWorkloadSnapshot?>(Snapshot);

    public Task SetStateAsync(Guid workloadId, string state, CancellationToken ct)
    {
        State = state;
        return Task.CompletedTask;
    }

    public Task RecordProvisionedAsync(
        Guid workloadId, string containerId, string? imageDigest, string networkId, CancellationToken ct)
    {
        Snapshot = Snapshot with { ContainerId = containerId };
        return Task.CompletedTask;
    }

    public Task RecordLimitsAsync(
        Guid workloadId, long cpuNanos, long memoryBytes, long storageBytes, CancellationToken ct) =>
        Task.CompletedTask;

    public Task RecordDeletedAsync(Guid workloadId, bool volumesRemoved, CancellationToken ct)
    {
        Deleted = true;
        VolumesRemoved = volumesRemoved;
        return Task.CompletedTask;
    }
}

internal sealed class FakeEngineRegistry : IDatabaseEngineRegistry
{
    public IReadOnlyList<IDatabaseEngine> All { get; } = EngineFactory.All();

    public IDatabaseEngine Get(DatabaseEngineKind kind) => All.Single(e => e.Kind == kind);
}
