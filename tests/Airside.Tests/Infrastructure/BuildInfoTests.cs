using Airside.Api.Features;

namespace Airside.Tests.Infrastructure;

public class BuildInfoTests
{
    [Fact]
    public void ReportsTheThreePartVersion()
    {
        // AssemblyName.Version is always four-part and would report 0.1.0.0 for a
        // 0.1.0 release. This value is compared against image tags, which never
        // carry a fourth component.
        Assert.Matches(@"^\d+\.\d+\.\d+$", BuildInfo.Version);
    }

    [Fact]
    public void MatchesTheVersionPrefixInDirectoryBuildProps()
    {
        Assert.Equal("0.1.0", BuildInfo.Version);
    }

    [Fact]
    public void CarriesNoBuildMetadata()
    {
        // SourceLink appends +<commit>; an image tag never does.
        Assert.DoesNotContain("+", BuildInfo.Version, StringComparison.Ordinal);
    }
}
