using Airside.Runtime.Applications;

namespace Airside.Tests.Registries;

/// <summary>
/// Finding the images a Dockerfile pulls from, so a private base image gets a credential.
/// </summary>
/// <remarks>
/// Without this a build against a private base fails at the daemon's own pull of
/// <c>FROM</c>, and the error names a missing image rather than a missing login —
/// sending the operator to look for a typo in a tag that is perfectly correct.
/// </remarks>
public class BaseImageTests
{
    [Fact]
    public void ASingleFromIsFound()
    {
        var images = BaseImages.Parse("FROM ghcr.io/myorg/base:1.2\nRUN echo hi\n");

        Assert.Equal("ghcr.io/myorg/base", Assert.Single(images).Repository);
    }

    [Fact]
    public void StageReferencesAreNotTreatedAsImages()
    {
        // "FROM builder" names an earlier stage, not a repository. Treating it as
        // one would have Airside look for a credential for a registry called
        // "builder".
        var images = BaseImages.Parse("""
        FROM ghcr.io/myorg/sdk:9 AS builder
        RUN dotnet publish
        FROM builder AS test
        FROM gcr.io/distroless/base
        COPY --from=builder /app /app
        """);

        Assert.Equal(2, images.Count);
        Assert.Equal("ghcr.io/myorg/sdk", images[0].Repository);
        Assert.Equal("gcr.io/distroless/base", images[1].Repository);
    }

    [Fact]
    public void PlatformFlagsAreSkipped()
    {
        var images = BaseImages.Parse("FROM --platform=linux/amd64 ghcr.io/myorg/base:1.0\n");

        Assert.Equal("ghcr.io/myorg/base", Assert.Single(images).Repository);
    }

    [Fact]
    public void ABuildArgumentInTheImagePositionIsSkipped()
    {
        // It cannot be resolved without evaluating the build, and guessing would
        // send a token to whatever the literal string happened to look like.
        Assert.Empty(BaseImages.Parse("ARG BASE\nFROM ${BASE}\n"));
        Assert.Empty(BaseImages.Parse("FROM $BASE\n"));
    }

    [Fact]
    public void CaseAndIndentationDoNotMatter()
    {
        var images = BaseImages.Parse("   from ghcr.io/myorg/base:1.0 as build\n");

        Assert.Equal("ghcr.io/myorg/base", Assert.Single(images).Repository);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RUN echo no from line here")]
    public void NothingToFindIsAnEmptyList(string? dockerfile) =>
        Assert.Empty(BaseImages.Parse(dockerfile));
}
