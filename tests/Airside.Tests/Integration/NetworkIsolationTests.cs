using System.Text;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Naming;

namespace Airside.Tests.Integration;

/// <summary>
/// That an application can only reach the databases it is attached to.
/// </summary>
/// <remarks>
/// <para>
/// This is the load-bearing security property of the whole design. Airside gives
/// every workload its own network and attaches an application to a database's
/// network only when somebody creates an attachment; the promise that follows is
/// that a compromised application cannot reach anything it was not granted. If
/// that promise is wrong, the attachment model is decoration.
/// </para>
/// <para>
/// So this runs against a real daemon. Docker's embedded DNS and its per-network
/// bridges are what actually enforce the boundary, and no fake can testify about
/// them. The test also asserts the positive case and re-attaches mid-way: a
/// negative result proves nothing unless the same rig can be made to succeed.
/// </para>
/// </remarks>
[Collection("docker")]
[Trait("Category", "Integration")]
public sealed class NetworkIsolationTests(IsolationFixture fixture) : IClassFixture<IsolationFixture>
{
    [DockerFact]
    public async Task AnApplicationReachesOnlyTheDatabasesItIsAttachedTo()
    {
        var ct = CancellationToken.None;

        // Attached: the application was connected to alpha's network at start-up,
        // exactly as the attach path does it.
        var alpha = await fixture.PingRedisAsync(IsolationFixture.AlphaName, ct);
        Assert.Contains("PONG", alpha.Output, StringComparison.Ordinal);
        Assert.Equal(0, alpha.ExitCode);

        // Not attached. Beta is running and healthy — the application simply has
        // no path to it, and Docker's DNS does not even resolve the name.
        var beta = await fixture.PingRedisAsync(IsolationFixture.BetaName, ct);
        Assert.DoesNotContain("PONG", beta.Output, StringComparison.Ordinal);
        Assert.NotEqual(0, beta.ExitCode);

        // The control. If beta stayed unreachable after an attach, the negative
        // above would only be telling us the rig is broken.
        await fixture.Runtime.Networks.ConnectAsync(IsolationFixture.BetaNetwork, fixture.AppId, ct);

        var betaAttached = await fixture.PingRedisAsync(IsolationFixture.BetaName, ct);
        Assert.Contains("PONG", betaAttached.Output, StringComparison.Ordinal);

        // And detaching takes the reach away again, which is what makes removing
        // an attachment mean something.
        await fixture.Runtime.Networks.DisconnectAsync(IsolationFixture.BetaNetwork, fixture.AppId, ct);

        var betaDetached = await fixture.PingRedisAsync(IsolationFixture.BetaName, ct);
        Assert.DoesNotContain("PONG", betaDetached.Output, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task TheProxyAdminApiIsUnreachableFromAWorkloadNetwork()
    {
        var ct = CancellationToken.None;

        // Caddy's admin API is unauthenticated and can load configuration that
        // executes commands. It binds 0.0.0.0 because the API talks to it from
        // another container, so the only thing standing between a workload and
        // the machine is that the workload is not on airside-internal.
        var blocked = await fixture.HttpGetAsync($"http://{IsolationFixture.ProxyName}:2019/config/", ct);
        Assert.NotEqual(0, blocked.ExitCode);
        Assert.DoesNotContain("apps", blocked.Output, StringComparison.Ordinal);

        // Control: the API's own network can reach it, so the failure above is
        // the boundary rather than a proxy that never started.
        await fixture.Runtime.Networks.ConnectAsync(IsolationFixture.InternalNetwork, fixture.AppId, ct);
        try
        {
            var allowed = await fixture.HttpGetAsync($"http://{IsolationFixture.ProxyName}:2019/config/", ct);
            Assert.Equal(0, allowed.ExitCode);
        }
        finally
        {
            await fixture.Runtime.Networks.DisconnectAsync(IsolationFixture.InternalNetwork, fixture.AppId, ct);
        }
    }
}

/// <summary>
/// Two databases, one application, and a proxy — built once for the class.
/// </summary>
/// <remarks>
/// Everything is created through <see cref="IContainerRuntime"/> rather than by
/// shelling out, so the test exercises the same code path that provisioning
/// uses. If Airside's own network handling were wrong, this rig would be wrong
/// in the same way and the test would say so.
/// </remarks>
public sealed class IsolationFixture : IAsyncLifetime
{
    private const string Prefix = "airside-it";
    private static readonly ImageReference Redis = new("redis", "7.4-alpine");
    private static readonly ImageReference Caddy = new("caddy", "2.8-alpine");
    private static readonly ContainerLimits Small = new(256L * 1024 * 1024, 500_000_000);

    private static Slug SlugOf(string value) => Slug.Create(value).Value;

    private readonly List<string> _containers = [];
    private readonly List<string> _networks = [];

    public IContainerRuntime Runtime { get; private set; } = null!;

    public static string AlphaName => $"{Prefix}-db-alpha";

    public static string BetaName => $"{Prefix}-db-beta";

    public static string ProxyName => $"{Prefix}-proxy";

    public static string BetaNetwork => AirsideNames.DatabaseNetwork(SlugOf("it-beta"));

    public static string InternalNetwork => $"{Prefix}-internal";

    public string AppId { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable)
        {
            return;
        }

        Runtime = DockerProbe.CreateRuntime();

        var ct = CancellationToken.None;
        var alphaNetwork = AirsideNames.DatabaseNetwork(SlugOf("it-alpha"));
        var appNetwork = AirsideNames.ApplicationNetwork(SlugOf("it-web"));

        foreach (var network in new[] { alphaNetwork, BetaNetwork, appNetwork, InternalNetwork })
        {
            await CreateNetworkAsync(network, ct);
        }

        await StartAsync(RedisSpec(AlphaName, alphaNetwork), ct);
        await StartAsync(RedisSpec(BetaName, BetaNetwork), ct);

        // Caddy with no config file: ContainerSpec has no host-bind-mount variant
        // by design, and CADDY_ADMIN is how the image takes an admin address. The
        // config itself is irrelevant here — what is under test is whether the
        // port can be reached at all. ContainerSecurity.Proxy rather than Default
        // because the Caddy binary carries file capabilities and will not exec
        // without NET_BIND_SERVICE in the bounding set.
        await StartAsync(new ContainerSpec
        {
            Name = ProxyName,
            Image = Caddy,
            Command = ["caddy", "run"],
            Environment = [new EnvironmentEntry("CADDY_ADMIN", new Secret("0.0.0.0:2019"), IsSensitive: false)],
            Labels = Labels(AirsideLabels.KindSystem, "it-proxy"),
            Limits = Small,
            NetworkName = InternalNetwork,
            RestartPolicy = RestartPolicy.No,
            Security = ContainerSecurity.Proxy,
        }, ct);

        // The application starts on its own network only. Attaching to alpha is a
        // separate act, which is the point.
        AppId = await StartAsync(new ContainerSpec
        {
            Name = $"{Prefix}-app-web",
            Image = Redis,
            Command = ["sleep", "600"],
            Labels = Labels(AirsideLabels.KindApplication, "it-web"),
            Limits = Small,
            NetworkName = appNetwork,
            RestartPolicy = RestartPolicy.No,
            Security = ContainerSecurity.DatabaseEngine,
        }, ct);

        await Runtime.Networks.ConnectAsync(alphaNetwork, AppId, ct);

        // Redis takes a moment to bind. Without this the first ping can fail for
        // a reason that has nothing to do with networking.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    /// <summary>Runs <c>redis-cli ping</c> from inside the application container.</summary>
    /// <remarks>
    /// A real client against a real engine, with a connect timeout so a route
    /// that exists but drops packets fails in seconds rather than hanging.
    /// </remarks>
    public Task<ProbeResult> PingRedisAsync(string host, CancellationToken ct) =>
        ExecAsync(["redis-cli", "-h", host, "-t", "3", "ping"], ct);

    public Task<ProbeResult> HttpGetAsync(string url, CancellationToken ct) =>
        ExecAsync(["wget", "-q", "-T", "3", "-O", "-", url], ct);

    private async Task<ProbeResult> ExecAsync(string[] argv, CancellationToken ct)
    {
        using var stdout = new MemoryStream();

        var result = await Runtime.Containers.ExecAsync(new ExecRequest(AppId, argv), stdout, ct);

        return new ProbeResult(result.ExitCode, Encoding.UTF8.GetString(stdout.ToArray()));
    }

    private static ContainerSpec RedisSpec(string name, string network) => new()
    {
        Name = name,
        Image = Redis,
        Labels = Labels(AirsideLabels.KindDatabase, name),
        Limits = Small,
        NetworkName = network,
        RestartPolicy = RestartPolicy.No,
        Security = ContainerSecurity.DatabaseEngine,
    };

    private static Dictionary<string, string> Labels(string kind, string slug) =>
        new(StringComparer.Ordinal)
        {
            [AirsideLabels.Managed] = AirsideLabels.True,
            [AirsideLabels.Kind] = kind,
            [AirsideLabels.Slug] = slug,
        };

    private async Task EnsureImageAsync(ImageReference image, CancellationToken ct)
    {
        if (await Runtime.Images.FindAsync(image, ct) is not null)
        {
            return;
        }

        await Runtime.Images.PullAsync(image, null, null, ct);
    }

    private async Task CreateNetworkAsync(string name, CancellationToken ct)
    {
        await Runtime.Networks.CreateAsync(
            new NetworkSpec(name, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AirsideLabels.Managed] = AirsideLabels.True,
            }),
            ct);

        _networks.Add(name);
    }

    private async Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
    {
        // Pulled rather than assumed present. Creating a container from an image
        // that is not cached fails with "No such image", and a developer machine
        // that happens to have it is the one place this never shows up.
        await EnsureImageAsync(spec.Image, ct);

        var existing = await Runtime.Containers.FindAsync(spec.Name, ct);

        if (existing is not null)
        {
            await Runtime.Containers.RemoveAsync(existing.Id, force: true, ct);
        }

        var id = await Runtime.Containers.CreateAsync(spec, ct);
        _containers.Add(id);

        await Runtime.Containers.StartAsync(id, ct);

        return id;
    }

    public async Task DisposeAsync()
    {
        if (Runtime is null)
        {
            return;
        }

        foreach (var id in _containers)
        {
            await SwallowAsync(() => Runtime.Containers.RemoveAsync(id, force: true, CancellationToken.None));
        }

        foreach (var network in _networks)
        {
            await SwallowAsync(() => Runtime.Networks.RemoveAsync(network, CancellationToken.None));
        }

        (Runtime as IDisposable)?.Dispose();
    }

    /// <summary>Teardown must not mask the failure that the test itself reported.</summary>
    private static async Task SwallowAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing useful to do. A leaked test container is a nuisance, not a
            // reason to turn a passing run red.
        }
    }
}

public sealed record ProbeResult(int ExitCode, string Output);
