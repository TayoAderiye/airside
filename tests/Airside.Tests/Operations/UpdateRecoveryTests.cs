using Airside.Core.Operations;

namespace Airside.Tests.Operations;

/// <summary>
/// That <c>state.json</c> tells an operator how to finish an update whose updater died.
/// </summary>
/// <remarks>
/// <para>
/// This is the failure the whole update design exists to survive. The process
/// performing the update is the process being replaced, so if it dies between
/// stopping the old container and starting the new one, there is no API running,
/// no job to retry, and nothing on the host that knows what was happening — except
/// this file.
/// </para>
/// <para>
/// What is under test is not that a file can be written, but that reading it back
/// leads to the <em>right</em> action. Advice that says "nothing changed" when the
/// old container is already gone would leave the host down while the operator
/// investigated calmly.
/// </para>
/// </remarks>
public class UpdateRecoveryTests
{
    private static UpdateState Sample(UpdateStep step) => new()
    {
        UpdateId = Guid.CreateVersion7(),
        FromVersion = "0.1.0",
        ToVersion = "0.2.0",
        FromImageDigest = "sha256:aaa",
        ToImageDigest = "sha256:bbb",
        Step = step,
        UpdatedAt = new DateTimeOffset(2026, 8, 16, 3, 0, 0, TimeSpan.Zero),
        BackupPath = "/var/lib/airside/backups/system.tar",
        AppliedMigrations = true,
    };

    [Fact]
    public void StateRoundTripsThroughTheFileFormat()
    {
        var original = Sample(UpdateStep.Swapping);

        var restored = UpdateState.FromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(original.UpdateId, restored!.UpdateId);
        Assert.Equal(UpdateStep.Swapping, restored.Step);
        Assert.Equal("sha256:aaa", restored.FromImageDigest);
        Assert.True(restored.AppliedMigrations);
    }

    [Fact]
    public void TheStepIsWrittenAsANameSoTheCliCanReadItWithoutTheEnum()
    {
        // The CLI is NativeAOT and parses the file with JsonDocument rather than
        // deserialising into this type. A numeric enum would leave it printing
        // "3" to an operator trying to work out what to do.
        Assert.Contains("\"step\": \"Swapping\"", Sample(UpdateStep.Swapping).ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedFileReadsAsNoStateRatherThanThrowing()
    {
        // Exactly what a process killed mid-write leaves behind, and exactly the
        // moment somebody is running the CLI. A stack trace here would be thrown
        // at the person trying to recover.
        Assert.Null(UpdateState.FromJson("{\"updateId\":\"01a0"));
        Assert.Null(UpdateState.FromJson(string.Empty));
    }

    [Theory]
    [InlineData(UpdateStep.Starting)]
    [InlineData(UpdateStep.BackingUp)]
    [InlineData(UpdateStep.Pulling)]
    public void BeforeTheSwapTheAdviceIsThatNothingChanged(UpdateStep step)
    {
        var advice = UpdateAdvice.For(step.ToString());

        Assert.Contains("running version is unchanged", advice, StringComparison.Ordinal);
        Assert.False(UpdateAdvice.MayBeOffline(step.ToString()));
    }

    [Fact]
    public void AtTheSwapTheAdviceIsToBringTheControlPlaneUp()
    {
        // The dangerous step: the old container may already be gone, so nothing
        // will retry on its own and the host has no API on it.
        var advice = UpdateAdvice.For(nameof(UpdateStep.Swapping));

        Assert.True(UpdateAdvice.MayBeOffline(nameof(UpdateStep.Swapping)));
        Assert.Contains("there may be no API running", advice, StringComparison.Ordinal);
        Assert.Contains("docker compose", advice, StringComparison.Ordinal);
        Assert.Contains("airside rollback", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterHealthCheckingTheAdviceSendsTheOperatorToTheLogs()
    {
        var advice = UpdateAdvice.For(nameof(UpdateStep.HealthChecking));

        Assert.Contains("docker logs airside-api", advice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UpdateStep.Succeeded)]
    [InlineData(UpdateStep.RolledBack)]
    [InlineData(UpdateStep.Failed)]
    public void ASettledUpdateNeedsNoAction(UpdateStep step) =>
        Assert.Contains("Nothing to do", UpdateAdvice.For(step.ToString()), StringComparison.Ordinal);

    [Fact]
    public void AStepFromANewerVersionProducesAMessageRatherThanACrash()
    {
        // A state file written by a version that added a step must not make the
        // recovery tool unusable — which is the one moment it cannot be.
        var advice = UpdateAdvice.For("SomethingAddedLater");

        Assert.Contains("Unrecognised state", advice, StringComparison.Ordinal);
        Assert.False(UpdateAdvice.MayBeOffline("SomethingAddedLater"));
    }
}
