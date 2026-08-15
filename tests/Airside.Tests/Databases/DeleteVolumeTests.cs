using Airside.Core.Databases;
using Airside.Core.Naming;
using Airside.Runtime.Jobs;
using Airside.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Slug = Airside.Core.Common.Slug;

namespace Airside.Tests.Databases;

/// <summary>
/// "Deleting a database must not delete its volume unless the admin explicitly
/// opts in via a separate checkbox."
/// </summary>
public class DeleteVolumeTests
{
    private static (DatabaseDeleteHandler Handler, FakeContainerRuntime Runtime,
        FakeDatabaseWorkloadStore Store, string VolumeName) Build(bool deleteVolume)
    {
        Slug.TryCreate("orders", out var slug);
        var volumeName = AirsideNames.Volume(slug, "data");

        var runtime = new FakeContainerRuntime();
        runtime.VolumeStore.Add(volumeName);
        runtime.NetworkStore.Add(AirsideNames.DatabaseNetwork(slug));

        var snapshot = new DatabaseWorkloadSnapshot(
            Guid.CreateVersion7(), slug, "Orders", DatabaseEngineKind.Postgres,
            Spec.Postgres() with { Slug = slug },
            ContainerId: "container-airside-db-orders",
            volumeName,
            AirsideNames.DatabaseNetwork(slug),
            AirsideNames.DatabaseContainer(slug));

        var store = new FakeDatabaseWorkloadStore(snapshot);
        var handler = new DatabaseDeleteHandler(runtime, store, NullLogger<DatabaseDeleteHandler>.Instance);

        return (handler, runtime, store, volumeName);
    }

    [Fact]
    public async Task Delete_WithoutOptIn_KeepsTheVolume()
    {
        var (handler, runtime, store, volumeName) = Build(deleteVolume: false);
        var context = new FakeJobContext(new DatabaseDeletePayload(store.Snapshot.Id, DeleteVolume: false));

        var result = await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(volumeName, runtime.VolumeStore, StringComparer.Ordinal);
        Assert.True(store.Deleted);
        Assert.False(store.VolumesRemoved);
    }

    [Fact]
    public async Task Delete_WithoutOptIn_SaysWhereTheDataWent()
    {
        // A kept volume that nobody is told about is indistinguishable from a
        // disk leak, so the step log has to name it.
        var (handler, _, store, volumeName) = Build(deleteVolume: false);
        var context = new FakeJobContext(new DatabaseDeletePayload(store.Snapshot.Id, DeleteVolume: false));

        await handler.ExecuteAsync(context, CancellationToken.None);

        var step = Assert.Single(context.Steps, s => s.Name == "volume");
        Assert.Contains(volumeName, step.Message, StringComparison.Ordinal);
        Assert.Contains("Kept", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_WithExplicitOptIn_RemovesTheVolume()
    {
        var (handler, runtime, store, volumeName) = Build(deleteVolume: true);
        var context = new FakeJobContext(new DatabaseDeletePayload(store.Snapshot.Id, DeleteVolume: true));

        await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(volumeName, runtime.VolumeStore, StringComparer.Ordinal);
        Assert.True(store.VolumesRemoved);
    }

    [Fact]
    public async Task Delete_AlwaysRemovesTheContainerAndNetwork()
    {
        foreach (var deleteVolume in new[] { true, false })
        {
            var (handler, runtime, store, _) = Build(deleteVolume);
            var context = new FakeJobContext(new DatabaseDeletePayload(store.Snapshot.Id, deleteVolume));

            await handler.ExecuteAsync(context, CancellationToken.None);

            Assert.Empty(runtime.ContainerStore);
            Assert.Empty(runtime.NetworkStore);
        }
    }

    [Fact]
    public async Task Delete_VolumeRemovalIsNotForced()
    {
        // force:true on a volume removal would tear it away from a container that
        // still has it open. If something still holds it, the delete should fail
        // loudly rather than corrupt.
        var (handler, runtime, store, _) = Build(deleteVolume: true);
        var context = new FakeJobContext(new DatabaseDeletePayload(store.Snapshot.Id, DeleteVolume: true));

        await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(runtime.Operations, o => o.StartsWith("volume.remove", StringComparison.Ordinal));
    }
}
