using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Naming;

namespace Airside.Tests.Runtime;

/// <summary>
/// The container-spec invariants that make the security rules structural rather
/// than a review checklist.
/// </summary>
public class ContainerSpecTests
{
    [Fact]
    public void VolumeMount_HasNoHostPathVariant()
    {
        // The strongest guarantee in the runtime layer: an arbitrary bind mount
        // is inexpressible, not merely rejected. If someone adds a host-path
        // constructor, this fails and the change gets the security review it needs.
        var constructors = typeof(VolumeMount).GetConstructors();

        foreach (var parameter in constructors.SelectMany(c => c.GetParameters()))
        {
            Assert.DoesNotContain("host", parameter.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source", parameter.Name!, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(constructors.SelectMany(c => c.GetParameters()), p => p.Name == "VolumeName");
    }

    [Fact]
    public void PortBinding_DefaultsToLoopback()
    {
        // Defaulting to 0.0.0.0 would put databases on the public internet within
        // a week of launch.
        var binding = new PortBinding(5432, 5432);

        Assert.Equal("127.0.0.1", binding.BindAddress);
        Assert.Equal(PortBinding.Loopback, binding.BindAddress);
    }

    [Fact]
    public void ContainerSecurity_DefaultDropsCapabilitiesAndPrivilegeEscalation()
    {
        Assert.True(ContainerSecurity.Default.NoNewPrivileges);
        Assert.Contains("ALL", ContainerSecurity.Default.DropCapabilities, StringComparer.Ordinal);
    }

    [Fact]
    public void ExecRequest_TakesAnArgumentVectorNotACommandLine()
    {
        var parameters = typeof(ExecRequest).GetConstructors().SelectMany(c => c.GetParameters()).ToList();
        var argv = parameters.Single(p => p.Name == "Argv");

        Assert.Equal(typeof(IReadOnlyList<string>), argv.ParameterType);
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(string) && p.Name == "Command");
    }

    [Fact]
    public void ImageReference_WithDigest_PinsByDigest()
    {
        // A tag moves. postgres:16 six months later is a different build, and a
        // restart landing on it is how a database comes back refusing to start.
        var pinned = new ImageReference("postgres", "16", "sha256:abc123");

        Assert.Equal("postgres@sha256:abc123", pinned.ToString());
        Assert.Equal("postgres:16", new ImageReference("postgres", "16").ToString());
    }

    [Fact]
    public void EnvironmentEntry_ValueIsAlwaysASecret()
    {
        // Even non-sensitive values, so serialising or logging a ContainerSpec
        // can never leak a password by accident.
        var value = typeof(EnvironmentEntry).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Single(p => p.Name == "Value");

        Assert.Equal(typeof(Secret), value.ParameterType);
    }
}

public class NamingTests
{
    private static Slug Slug(string value)
    {
        Assert.True(Airside.Core.Common.Slug.TryCreate(value, out var slug));
        return slug;
    }

    [Fact]
    public void DerivedNames_ContainNothingButTheValidatedSlug()
    {
        var slug = Slug("orders-db");

        Assert.Equal("airside-db-orders-db", AirsideNames.DatabaseContainer(slug));
        Assert.Equal("airside-vol-orders-db-data", AirsideNames.Volume(slug, "data"));
        Assert.Equal("airside-net-db-orders-db", AirsideNames.DatabaseNetwork(slug));
        Assert.Equal("airside-net-app-orders-db", AirsideNames.ApplicationNetwork(slug));
    }

    [Fact]
    public void ApplicationContainer_IncludesTheDeployment()
    {
        // Blue/green needs two containers for one application at once, so the
        // name cannot be derived from the slug alone.
        var deployment = Guid.CreateVersion7();
        var name = AirsideNames.ApplicationContainer(Slug("web"), deployment);

        Assert.StartsWith("airside-app-web-", name, StringComparison.Ordinal);
        Assert.EndsWith(deployment.ToString("N")[..8], name, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemContainerNames_AreTheThreeProtectedOnes()
    {
        Assert.Equal(3, AirsideLabels.SystemContainers.All.Count);
        Assert.Contains("airside-api", AirsideLabels.SystemContainers.All, StringComparer.Ordinal);
        Assert.Contains("airside-db", AirsideLabels.SystemContainers.All, StringComparer.Ordinal);
        Assert.Contains("airside-proxy", AirsideLabels.SystemContainers.All, StringComparer.Ordinal);
    }

    [Fact]
    public void KeyRingPath_IsOnTheHostNotInTheContainer()
    {
        // If the key ring lived inside the image it would be lost on the first
        // self-update, taking every stored secret with it.
        Assert.StartsWith("/var/lib/airside", AirsideLabels.HostPaths.KeyRing, StringComparison.Ordinal);
        Assert.StartsWith("/var/lib/airside", AirsideLabels.HostPaths.State, StringComparison.Ordinal);
    }
}
