using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Naming;

namespace Airside.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IContainerRuntime"/>.
/// </summary>
/// <remarks>
/// Unit tests must not need a Docker daemon. This models enough of the runtime to
/// exercise the interesting behaviour — creation, removal, and what a health
/// check reports — and records what was asked of it so a test can assert that a
/// failed job removed exactly what it created and nothing else.
/// </remarks>
public sealed class FakeContainerRuntime : IContainerRuntime
{
    public FakeContainerRuntime()
    {
        Containers = new FakeContainerOperations(this);
        Images = new FakeImageOperations();
        Volumes = new FakeVolumeOperations(this);
        Networks = new FakeNetworkOperations(this);
    }

    public IContainerOperations Containers { get; }

    public IImageOperations Images { get; }

    public IVolumeOperations Volumes { get; }

    public INetworkOperations Networks { get; }

    public Dictionary<string, ContainerSummary> ContainerStore { get; } = new(StringComparer.Ordinal);

    public HashSet<string> VolumeStore { get; } = new(StringComparer.Ordinal);

    public HashSet<string> NetworkStore { get; } = new(StringComparer.Ordinal);

    /// <summary>What a started container reports. Set to Unhealthy to exercise the failure path.</summary>
    public ContainerHealth HealthOnStart { get; set; } = ContainerHealth.Healthy;

    public List<string> Operations { get; } = [];

    public Task<RuntimeInfo> GetInfoAsync(CancellationToken ct) =>
        Task.FromResult(new RuntimeInfo("1.43", "test", "linux", "test", 4, 8L * 1024 * 1024 * 1024));

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    internal void Record(string operation) => Operations.Add(operation);
}

internal sealed class FakeContainerOperations(FakeContainerRuntime parent) : IContainerOperations
{
    public Task<string> CreateAsync(ContainerSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var id = $"container-{spec.Name}";
        parent.Record($"container.create:{spec.Name}");

        parent.ContainerStore[id] = new ContainerSummary(
            id, spec.Name, spec.Image.ToString(), "sha256:test",
            ContainerRunState.Created, DateTimeOffset.UnixEpoch, null, null,
            ContainerHealth.Starting, spec.Labels, spec.NetworkName is null ? [] : [spec.NetworkName]);

        return Task.FromResult(id);
    }

    public Task StartAsync(string containerId, CancellationToken ct)
    {
        parent.Record($"container.start:{containerId}");

        if (parent.ContainerStore.TryGetValue(containerId, out var container))
        {
            parent.ContainerStore[containerId] = container with
            {
                State = parent.HealthOnStart == ContainerHealth.Unhealthy
                    ? ContainerRunState.Exited
                    : ContainerRunState.Running,
                Health = parent.HealthOnStart,
                ExitCode = parent.HealthOnStart == ContainerHealth.Unhealthy ? 1 : null,
            };
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(string containerId, TimeSpan timeout, CancellationToken ct)
    {
        parent.Record($"container.stop:{containerId}");
        return Task.CompletedTask;
    }

    public Task RestartAsync(string containerId, TimeSpan timeout, CancellationToken ct)
    {
        parent.Record($"container.restart:{containerId}");
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string containerId, bool force, CancellationToken ct)
    {
        parent.Record($"container.remove:{containerId}");
        parent.ContainerStore.Remove(containerId);
        return Task.CompletedTask;
    }

    public Task<ContainerSummary?> FindAsync(string idOrName, CancellationToken ct)
    {
        if (parent.ContainerStore.TryGetValue(idOrName, out var byId))
        {
            return Task.FromResult<ContainerSummary?>(byId);
        }

        return Task.FromResult(parent.ContainerStore.Values.FirstOrDefault(c =>
            string.Equals(c.Name, idOrName, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<ContainerSummary>> ListManagedAsync(
        IReadOnlyDictionary<string, string>? labelFilters,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ContainerSummary>>([.. parent.ContainerStore.Values]);

    public async IAsyncEnumerable<ContainerLogLine> StreamLogsAsync(
        string containerId,
        LogQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public Task<ContainerStatsSample?> SampleStatsAsync(string containerId, CancellationToken ct) =>
        Task.FromResult<ContainerStatsSample?>(null);

    public Task<ExecResult> ExecAsync(ExecRequest request, Stream? standardOutput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        parent.Record($"exec:{string.Join(' ', request.Argv)}");
        return Task.FromResult(new ExecResult(0, string.Empty));
    }

    public Task CopyIntoContainerAsync(
        string containerId,
        string containerPath,
        string fileName,
        Stream content,
        CancellationToken ct)
    {
        parent.Record($"container.copyinto:{containerId}:{containerPath}/{fileName}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeImageOperations : IImageOperations
{
    public Task<ImageSummary> PullAsync(ImageReference image, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Task.FromResult(new ImageSummary(
            "img", "sha256:pulled", [image.ToString()], 1024, DateTimeOffset.UnixEpoch));
    }

    public Task<ImageSummary> BuildAsync(ImageBuildRequest request, IProgress<string>? progress, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<ImageSummary?> FindAsync(ImageReference image, CancellationToken ct) =>
        Task.FromResult<ImageSummary?>(null);

    public Task RemoveAsync(string imageId, bool force, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeVolumeOperations(FakeContainerRuntime parent) : IVolumeOperations
{
    public Task<VolumeSummary> CreateAsync(VolumeSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        parent.Record($"volume.create:{spec.Name}");
        parent.VolumeStore.Add(spec.Name);
        return Task.FromResult(new VolumeSummary(spec.Name, $"/mnt/{spec.Name}", DateTimeOffset.UnixEpoch, spec.Labels));
    }

    public Task<VolumeSummary?> FindAsync(string name, CancellationToken ct) =>
        Task.FromResult(parent.VolumeStore.Contains(name)
            ? new VolumeSummary(name, $"/mnt/{name}", DateTimeOffset.UnixEpoch,
                new Dictionary<string, string>(StringComparer.Ordinal))
            : null);

    public Task<IReadOnlyList<VolumeSummary>> ListManagedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VolumeSummary>>([]);

    public Task RemoveAsync(string name, bool force, CancellationToken ct)
    {
        parent.Record($"volume.remove:{name}");
        parent.VolumeStore.Remove(name);
        return Task.CompletedTask;
    }

    public Task<long> MeasureAsync(string name, CancellationToken ct) => Task.FromResult(0L);

    public Task CopyFromAsync(string volumeName, string path, Stream destination, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task CopyIntoAsync(string volumeName, string path, Stream source, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeNetworkOperations(FakeContainerRuntime parent) : INetworkOperations
{
    public Task<NetworkSummary> CreateAsync(NetworkSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        parent.Record($"network.create:{spec.Name}");
        parent.NetworkStore.Add(spec.Name);
        return Task.FromResult(new NetworkSummary($"net-{spec.Name}", spec.Name, spec.Labels, []));
    }

    public Task<NetworkSummary?> FindAsync(string name, CancellationToken ct) =>
        Task.FromResult(parent.NetworkStore.Contains(name)
            ? new NetworkSummary($"net-{name}", name, new Dictionary<string, string>(StringComparer.Ordinal), [])
            : null);

    public Task<IReadOnlyList<NetworkSummary>> ListManagedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NetworkSummary>>([]);

    public Task RemoveAsync(string name, CancellationToken ct)
    {
        parent.Record($"network.remove:{name}");
        parent.NetworkStore.Remove(name);
        return Task.CompletedTask;
    }

    public Task ConnectAsync(string networkName, string containerId, CancellationToken ct)
    {
        parent.Record($"network.connect:{networkName}:{containerId}");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string networkName, string containerId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Builds the real engines against a fake runtime.</summary>
public static class EngineFactory
{
    public static IDatabaseEngine Postgres() => Create(DatabaseEngineKind.Postgres);

    public static IDatabaseEngine MySql() => Create(DatabaseEngineKind.MySql);

    public static IDatabaseEngine MongoDb() => Create(DatabaseEngineKind.MongoDb);

    public static IDatabaseEngine Redis() => Create(DatabaseEngineKind.Redis);

    public static IReadOnlyList<IDatabaseEngine> All() => [Postgres(), MySql(), MongoDb(), Redis()];

    public static IReadOnlyList<(IDatabaseEngine Engine, DatabaseProvisionSpec Spec)> AllWithSpecs() =>
    [
        (Postgres(), Spec.Postgres()),
        (MySql(), Spec.MySql()),
        (MongoDb(), Spec.MongoDb()),
        (Redis(), Spec.Redis()),
    ];

    private static IDatabaseEngine Create(DatabaseEngineKind kind)
    {
        var runtime = new FakeContainerRuntime();

        // Reflection because the engines are internal to Airside.Runtime, which is
        // correct — nothing outside that assembly should construct one. The
        // registry is the supported way in, and this is a test reaching past it
        // deliberately rather than a reason to widen the surface.
        var type = typeof(Airside.Runtime.DependencyInjection).Assembly
            .GetTypes()
            .Single(t => t.Name == $"{Name(kind)}Engine");

        return (IDatabaseEngine)Activator.CreateInstance(type, runtime)!;
    }

    private static string Name(DatabaseEngineKind kind) => kind switch
    {
        DatabaseEngineKind.Postgres => "Postgres",
        DatabaseEngineKind.MySql => "MySql",
        DatabaseEngineKind.MongoDb => "MongoDb",
        DatabaseEngineKind.Redis => "Redis",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>Valid provisioning specs, one per engine, for tests to vary from.</summary>
public static class Spec
{
    private const long Gib = 1024L * 1024 * 1024;

    public static DatabaseProvisionSpec Postgres() => Base() with
    {
        Engine = DatabaseEngineKind.Postgres,
        Version = "16",
        DatabaseName = "orders",
        Username = "app",
    };

    public static DatabaseProvisionSpec MySql() => Base() with
    {
        Engine = DatabaseEngineKind.MySql,
        Version = "8.4",
        DatabaseName = "orders",
        Username = "app",
    };

    public static DatabaseProvisionSpec MongoDb() => Base() with
    {
        Engine = DatabaseEngineKind.MongoDb,
        Version = "8.0",
        DatabaseName = "orders",
        Username = "root",
    };

    public static DatabaseProvisionSpec Redis() => Base() with
    {
        Engine = DatabaseEngineKind.Redis,
        Version = "7.4",
        MaxMemoryBytes = (long)(2 * Gib * 0.70),
        MaxMemoryPolicy = "allkeys-lru",
        AofEnabled = false,
    };

    private static DatabaseProvisionSpec Base()
    {
        Slug.TryCreate("workload", out var slug);

        return new DatabaseProvisionSpec
        {
            WorkloadId = Guid.CreateVersion7(),
            Slug = slug,
            DisplayName = "Workload",
            Engine = DatabaseEngineKind.Postgres,
            Version = "16",
            CpuNanos = 1_000_000_000,
            MemoryBytes = 2 * Gib,
            StorageBytes = 10 * Gib,
            Password = new Secret("generated-password"),
        };
    }
}
