using System.Text.Json;
using Airside.Core.Operations;

namespace Airside.Tests.Operations;

/// <summary>
/// The dashboard digests in <c>state.json</c>, which two programs disagree about at their peril.
/// </summary>
/// <remarks>
/// <para>
/// The API writes this file by serialising <see cref="UpdateState"/>. The CLI
/// reads it by property name, on purpose — it is NativeAOT and has to keep
/// working against a file written by a newer version. The cost of that choice is
/// that a rename on the writing side does not break the reading side; it makes it
/// silently return null, and <c>airside rollback</c> then prints instructions
/// that quietly omit the dashboard.
/// </para>
/// <para>
/// So the wire names are asserted here as literals, matching the strings the CLI
/// passes to <c>Text(root, …)</c>.
/// </para>
/// </remarks>
public class UpdateStateUiDigestTests
{
    private static UpdateState Sample() => new()
    {
        UpdateId = Guid.CreateVersion7(),
        FromVersion = "0.1.0",
        ToVersion = "0.2.0",
        FromImageDigest = "sha256:api-old",
        ToImageDigest = "sha256:api-new",
        FromUiImageDigest = "sha256:ui-old",
        ToUiImageDigest = "sha256:ui-new",
        Step = UpdateStep.Swapping,
        UpdatedAt = new DateTimeOffset(2026, 8, 16, 3, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void TheDashboardDigestsAreWrittenUnderTheNamesTheCliReads()
    {
        using var document = JsonDocument.Parse(Sample().ToJson());
        var root = document.RootElement;

        Assert.Equal("sha256:ui-old", root.GetProperty("fromUiImageDigest").GetString());
        Assert.Equal("sha256:ui-new", root.GetProperty("toUiImageDigest").GetString());
    }

    [Fact]
    public void TheDashboardDigestsSurviveARoundTrip()
    {
        var restored = UpdateState.FromJson(Sample().ToJson());

        Assert.NotNull(restored);
        Assert.Equal("sha256:ui-old", restored!.FromUiImageDigest);
        Assert.Equal("sha256:ui-new", restored.ToUiImageDigest);
    }

    [Fact]
    public void AStateFileWrittenBeforeTheDashboardExistedStillParses()
    {
        // An update in flight when this version arrives left a file with no
        // dashboard fields at all. That file is read by the code responsible for
        // recovering the update — so if it failed to parse, the one artefact
        // recovery depends on would be lost at exactly the moment it is needed.
        var legacy = """
        {
          "updateId": "01920000-0000-7000-8000-000000000000",
          "fromVersion": "0.1.0",
          "toVersion": "0.2.0",
          "fromImageDigest": "sha256:api-old",
          "toImageDigest": "sha256:api-new",
          "step": "Swapping",
          "updatedAt": "2026-08-16T03:00:00+00:00",
          "appliedMigrations": true
        }
        """;

        var restored = UpdateState.FromJson(legacy);

        Assert.NotNull(restored);
        Assert.Equal("sha256:api-old", restored!.FromImageDigest);
        Assert.Null(restored.FromUiImageDigest);
        Assert.True(restored.AppliedMigrations);
    }
}
