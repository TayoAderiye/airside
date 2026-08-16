using Airside.Api.Features;
using Airside.Core.Containers;
using Airside.Core.Naming;
using Airside.Tests.Fakes;

namespace Airside.Tests.Api;

/// <summary>
/// That Airside's own containers appear in the lists, and stay inert there.
/// </summary>
/// <remarks>
/// <para>
/// Both summary DTOs carried an <c>IsSystem</c> flag from the beginning, the
/// compose file labels these containers <c>airside.system=true</c>, and its
/// comment says they are visible in the UI. The flag was hardcoded false and
/// the label was never read, so a working install showed two empty lists with
/// four containers running.
/// </para>
/// <para>
/// The safety property is the interesting half. These entries carry ids that
/// exist in no table, so every lifecycle and detail endpoint looks them up,
/// finds nothing, and answers 404 — stopping the API through the dashboard the
/// API is serving is unreachable without anyone having to remember a guard.
/// </para>
/// </remarks>
public sealed class SystemWorkloadReaderTests
{
    private static FakeContainerRuntime RuntimeWith(params string[] names)
    {
        var runtime = new FakeContainerRuntime();

        foreach (var name in names)
        {
            runtime.ContainerStore[name] = new ContainerSummary(
                name,
                name,
                name == AirsideLabels.SystemContainers.Database
                    ? "postgres:16.6-alpine"
                    : "ghcr.io/tayoaderiye/airside:0.1.4",
                ImageDigest: "sha256:abc",
                ContainerRunState.Running,
                CreatedAt: DateTimeOffset.UnixEpoch,
                StartedAt: DateTimeOffset.UnixEpoch,
                ExitCode: null,
                ContainerHealth.Healthy,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["airside.system"] = "true" },
                [],
                []);
        }

        return runtime;
    }

    [Fact]
    public async Task TheApiUiAndProxyAppearAsApplications()
    {
        var reader = new SystemWorkloadReader(RuntimeWith(
            AirsideLabels.SystemContainers.Api,
            AirsideLabels.SystemContainers.Ui,
            AirsideLabels.SystemContainers.Proxy));

        var apps = await reader.ApplicationsAsync(CancellationToken.None);

        Assert.Equal(3, apps.Count);
        Assert.All(apps, a => Assert.True(a.IsSystem));
        Assert.Contains(apps, a => a.Slug == AirsideLabels.SystemContainers.Api);
        Assert.Contains(apps, a => a.Slug == AirsideLabels.SystemContainers.Ui);
    }

    [Fact]
    public async Task TheControlPlaneStoreAppearsAsADatabase()
    {
        var reader = new SystemWorkloadReader(RuntimeWith(AirsideLabels.SystemContainers.Database));

        var database = Assert.Single(await reader.DatabasesAsync(CancellationToken.None));

        Assert.True(database.IsSystem);
        Assert.Equal("postgres", database.Engine);

        // Read from the image tag, so it reports what is actually running rather
        // than what the compose file said when it was written.
        Assert.Equal("16.6-alpine", database.Version);
    }

    [Fact]
    public async Task NothingIsReportedUnderTheSqliteStore()
    {
        // No airside-db container exists under SQLite. Reporting one would be a
        // lie about the install, and the entry would never resolve to anything.
        var reader = new SystemWorkloadReader(RuntimeWith(AirsideLabels.SystemContainers.Api));

        Assert.Empty(await reader.DatabasesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AContainerThatIsNotRunningIsNotReportedAsRunning()
    {
        var runtime = RuntimeWith(AirsideLabels.SystemContainers.Api);
        runtime.ContainerStore[AirsideLabels.SystemContainers.Api] =
            runtime.ContainerStore[AirsideLabels.SystemContainers.Api] with
            {
                State = ContainerRunState.Exited,
            };

        var app = Assert.Single(await new SystemWorkloadReader(runtime).ApplicationsAsync(CancellationToken.None));

        Assert.Equal("stopped", app.State);
    }

    [Fact]
    public async Task AnUnhealthyContainerSaysSoRatherThanRunning()
    {
        // Running and unhealthy is the state that matters most on this list: the
        // container is up, and the thing inside it is not answering.
        var runtime = RuntimeWith(AirsideLabels.SystemContainers.Api);
        runtime.ContainerStore[AirsideLabels.SystemContainers.Api] =
            runtime.ContainerStore[AirsideLabels.SystemContainers.Api] with
            {
                Health = ContainerHealth.Unhealthy,
            };

        var app = Assert.Single(await new SystemWorkloadReader(runtime).ApplicationsAsync(CancellationToken.None));

        Assert.Equal("unhealthy", app.State);
    }

    [Fact]
    public async Task TheSameContainerKeepsTheSameIdAcrossCalls()
    {
        // A fresh id per request would reorder and re-render the list on every
        // poll, and would make any future link to one of these meaningless.
        var reader = new SystemWorkloadReader(RuntimeWith(AirsideLabels.SystemContainers.Api));

        var first = Assert.Single(await reader.ApplicationsAsync(CancellationToken.None));
        var second = Assert.Single(await reader.ApplicationsAsync(CancellationToken.None));

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(Guid.Empty, first.Id);
    }

    [Fact]
    public async Task NoResourceAllocationIsClaimed()
    {
        // Airside did not set these limits and does not know them. Reporting a
        // number read back out of Docker would put the control plane into the
        // host allocation arithmetic, where it does not belong.
        var reader = new SystemWorkloadReader(RuntimeWith(AirsideLabels.SystemContainers.Api));

        var app = Assert.Single(await reader.ApplicationsAsync(CancellationToken.None));

        Assert.Equal(0, app.CpuNanos);
        Assert.Equal(0, app.MemoryBytes);
    }
}
