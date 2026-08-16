using Airside.Core.Containers;
using Airside.Core.Naming;

namespace Airside.Tests.Integration;

/// <summary>
/// That the security profiles let ordinary images actually start.
/// </summary>
/// <remarks>
/// <para>
/// A capability set is easy to get wrong in the safe-looking direction. Dropping
/// everything reads as the strictest choice and passes review, and it is only
/// discovered to be unusable when somebody deploys a real image — the failure
/// arrives as <c>chown … Operation not permitted</c> from inside the image's own
/// entrypoint, which does not look like an Airside problem at all.
/// </para>
/// <para>
/// That is exactly how this was found, and late: the first deployment tested was
/// a hand-written Dockerfile running as root on a high port, which is the one
/// shape that survives a bare <c>CapDrop=ALL</c>. So the profiles are pinned here
/// against the stock images real users deploy.
/// </para>
/// </remarks>
[Collection("docker")]
[Trait("Category", "Integration")]
public sealed class StockImageStartupTests
{
    /// <summary>nginx: de-escalates to uid 101 and binds port 80.</summary>
    [DockerFact]
    public async Task AStockWebImageStartsUnderTheApplicationProfile() =>
        await AssertStaysUpAsync(
            "airside-it-nginx",
            new ImageReference("nginx", "alpine"),
            command: null,
            ContainerSecurity.Application);

    /// <summary>
    /// The same image under the old profile, to show the test can fail.
    /// </summary>
    /// <remarks>
    /// Without this, a future change that quietly widened <c>Default</c> would
    /// make the test above pass for the wrong reason and nobody would notice.
    /// </remarks>
    [DockerFact]
    public async Task TheSameImageDoesNotStartWithEveryCapabilityDropped()
    {
        var stayedUp = await StaysUpAsync(
            "airside-it-nginx-bare",
            new ImageReference("nginx", "alpine"),
            command: null,
            ContainerSecurity.Default);

        Assert.False(stayedUp, "nginx started with all capabilities dropped, so this test no longer proves anything.");
    }

    /// <summary>Caddy, whose binary carries file capabilities.</summary>
    /// <remarks>
    /// A different failure from nginx's: the kernel refuses the <c>execve</c>
    /// outright, so the container dies before the proxy logs a single line.
    /// </remarks>
    [DockerFact]
    public async Task TheProxyImageStartsUnderTheProxyProfile() =>
        await AssertStaysUpAsync(
            "airside-it-caddy",
            new ImageReference("caddy", "2.8-alpine"),
            ["caddy", "run"],
            ContainerSecurity.Proxy);

    private static async Task AssertStaysUpAsync(
        string name,
        ImageReference image,
        IReadOnlyList<string>? command,
        ContainerSecurity security)
    {
        Assert.True(
            await StaysUpAsync(name, image, command, security),
            $"{image} did not stay running. Its entrypoint needs a capability the profile does not grant.");
    }

    private static async Task<bool> StaysUpAsync(
        string name,
        ImageReference image,
        IReadOnlyList<string>? command,
        ContainerSecurity security)
    {
        using var runtime = (IDisposable)DockerProbe.CreateRuntime();
        var containers = ((IContainerRuntime)runtime).Containers;

        // As above: the image has to be present before a container can be made
        // from it, and a clean runner has nothing cached.
        var images = ((IContainerRuntime)runtime).Images;

        if (await images.FindAsync(image, CancellationToken.None) is null)
        {
            await images.PullAsync(image, null, null, CancellationToken.None);
        }

        var existing = await containers.FindAsync(name, CancellationToken.None);

        if (existing is not null)
        {
            await containers.RemoveAsync(existing.Id, force: true, CancellationToken.None);
        }

        var id = await containers.CreateAsync(
            new ContainerSpec
            {
                Name = name,
                Image = image,
                Command = command,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AirsideLabels.Managed] = AirsideLabels.True,
                    [AirsideLabels.Kind] = AirsideLabels.KindApplication,
                    [AirsideLabels.Slug] = name,
                },
                Limits = new ContainerLimits(256L * 1024 * 1024, 500_000_000),

                // No restart policy: a crash-looping container reports as running
                // between attempts, and the whole question here is whether it
                // stayed up.
                RestartPolicy = RestartPolicy.No,
                Security = security,
            },
            CancellationToken.None);

        try
        {
            await containers.StartAsync(id, CancellationToken.None);

            // Long enough for an entrypoint to reach the chown or the bind that
            // fails. These images either serve or die within a second.
            await Task.Delay(TimeSpan.FromSeconds(3));

            var summary = await containers.FindAsync(id, CancellationToken.None);

            return summary?.State == ContainerRunState.Running;
        }
        finally
        {
            await containers.RemoveAsync(id, force: true, CancellationToken.None);
        }
    }
}
