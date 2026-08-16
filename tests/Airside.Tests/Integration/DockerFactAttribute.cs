using Airside.Core.Containers;
using Airside.Runtime.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using DD = Docker.DotNet;

namespace Airside.Tests.Integration;

/// <summary>
/// A fact that needs a real Docker daemon, and skips rather than fails without one.
/// </summary>
/// <remarks>
/// <para>
/// These tests assert things no fake can tell us. Whether one container can open
/// a socket to another is a property of Docker's networking, not of Airside's
/// code, so a mocked runtime that returned "not reachable" would prove only that
/// the mock said so.
/// </para>
/// <para>
/// Skipping is the right behaviour on a machine with no daemon — a contributor
/// running <c>dotnet test</c> on a laptop should not see red for something they
/// did not break. CI runs with Docker present, so the tests are not optional
/// there. Set <c>AIRSIDE_REQUIRE_DOCKER=1</c> to turn a missing daemon into a
/// failure, which is what CI does so a broken daemon cannot silently skip the
/// isolation test.
/// </para>
/// </remarks>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (DockerProbe.IsAvailable)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("AIRSIDE_REQUIRE_DOCKER") == "1")
        {
            throw new InvalidOperationException(
                "AIRSIDE_REQUIRE_DOCKER=1 but no Docker daemon answered. These tests must not be skipped in CI.");
        }

        Skip = "No Docker daemon is available.";
    }
}

internal static class DockerProbe
{
    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            using var client = new DD.DockerClientConfiguration().CreateClient();
            using var runtime = new DockerContainerRuntime(client, NullLoggerFactory.Instance);

            return runtime.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
#pragma warning disable CA1031 // Any failure to reach the daemon means the same thing here.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    });

    public static bool IsAvailable => Probe.Value;

    public static IContainerRuntime CreateRuntime() =>
        new DockerContainerRuntime(
            new DD.DockerClientConfiguration().CreateClient(),
            NullLoggerFactory.Instance,
            ownsClient: true);
}
