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
        // Read from the file rather than asserted as a literal. The literal made
        // this fail on every release, which trains whoever is cutting it to edit
        // the test until it goes green — and the one thing it exists to catch is
        // the version disagreeing with what shipped.
        Assert.Equal(DeclaredVersionPrefix(), BuildInfo.Version);
    }

    [Fact]
    public void CarriesNoBuildMetadata()
    {
        // SourceLink appends +<commit>; an image tag never does.
        Assert.DoesNotContain("+", BuildInfo.Version, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>VersionPrefix</c> as the build actually declares it.
    /// </summary>
    /// <remarks>
    /// Found by walking up from the test assembly, because the working directory
    /// during a test run is the output folder and nothing hands it the repository
    /// root.
    /// </remarks>
    private static string DeclaredVersionPrefix()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Directory.Build.props");

            if (File.Exists(candidate))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    File.ReadAllText(candidate),
                    @"<VersionPrefix>([^<]+)</VersionPrefix>");

                Assert.True(match.Success, $"{candidate} declares no VersionPrefix.");

                return match.Groups[1].Value;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No Directory.Build.props above {AppContext.BaseDirectory}.");
    }
}
