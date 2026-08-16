using Airside.Core.Containers;

namespace Airside.Tests.Registries;

/// <summary>
/// Which registry an image reference names.
/// </summary>
/// <remarks>
/// <para>
/// The rule is not "the part before the first slash". Docker treats the first
/// component as a registry only if it looks like a host — it contains a dot or a
/// colon, or is exactly <c>localhost</c>. Everything else is a Docker Hub
/// namespace.
/// </para>
/// <para>
/// Getting it backwards is quiet and bad in both directions: it either sends a
/// private registry's token to Docker Hub, or looks for a Hub credential under a
/// registry name that can never match, leaving a pull failing as
/// image-not-found while the credential sits there looking correct.
/// </para>
/// </remarks>
public class RegistryHostTests
{
    [Theory]
    [InlineData("nginx", "docker.io")]
    [InlineData("library/nginx", "docker.io")]
    [InlineData("myorg/app", "docker.io")]
    public void ANameWithoutAHostIsDockerHub(string repository, string expected) =>
        Assert.Equal(expected, RegistryHost.Of(repository));

    [Theory]
    [InlineData("ghcr.io/tayo/airside", "ghcr.io")]
    [InlineData("quay.io/prometheus/node-exporter", "quay.io")]
    [InlineData("123456789.dkr.ecr.eu-west-1.amazonaws.com/app", "123456789.dkr.ecr.eu-west-1.amazonaws.com")]
    public void ADottedFirstComponentIsARegistry(string repository, string expected) =>
        Assert.Equal(expected, RegistryHost.Of(repository));

    [Fact]
    public void TheDistinctionIsADotNotASlash()
    {
        // The pair that makes the rule matter. One is an image owned by "myorg"
        // on Docker Hub; the other is an image on a registry called "myorg.io".
        Assert.Equal("docker.io", RegistryHost.Of("myorg/app"));
        Assert.Equal("myorg.io", RegistryHost.Of("myorg.io/app"));
    }

    [Theory]
    [InlineData("registry.internal:5000/app", "registry.internal:5000")]
    [InlineData("localhost:5000/app", "localhost:5000")]
    [InlineData("localhost/app", "localhost")]
    public void APortOrLocalhostAlsoMakesItARegistry(string repository, string expected) =>
        Assert.Equal(expected, RegistryHost.Of(repository));

    [Theory]
    [InlineData("docker.io/library/nginx")]
    [InlineData("index.docker.io/library/nginx")]
    [InlineData("registry-1.docker.io/library/nginx")]
    public void HubsSeveralNamesAllResolveToOneEntry(string repository)
    {
        // Otherwise a credential saved for "docker.io" would not match an image
        // written as "index.docker.io/...", and the two would drift apart with
        // nothing to say why one worked and one did not.
        Assert.Equal(RegistryHost.DockerHub, RegistryHost.Of(repository));
    }

    [Theory]
    [InlineData("ghcr.io", "ghcr.io")]
    [InlineData("https://ghcr.io", "ghcr.io")]
    [InlineData("https://ghcr.io/", "ghcr.io")]
    [InlineData("http://registry.internal:5000/", "registry.internal:5000")]
    [InlineData("ghcr.io/myorg", "ghcr.io")]
    [InlineData("GHCR.IO", "ghcr.io")]
    public void WhatPeopleActuallyPasteIsNormalised(string input, string expected)
    {
        // Rejecting these would be defensible and useless: the result is a
        // credential that saves successfully, never matches an image, and gives
        // no clue why.
        Assert.Equal(expected, RegistryHost.Normalise(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyRegistryMeansDockerHub(string? input) =>
        Assert.Equal(RegistryHost.DockerHub, RegistryHost.Normalise(input));

    [Fact]
    public void AParsedReferenceResolvesTheSameWayAsItsRepository()
    {
        var image = ImageReference.Parse("ghcr.io/tayo/airside:0.1.0");

        Assert.Equal("ghcr.io", RegistryHost.Of(image));
    }
}
