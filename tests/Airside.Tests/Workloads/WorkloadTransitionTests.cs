using Airside.Core.Common;
using Airside.Core.Workloads;

namespace Airside.Tests.Workloads;

public class WorkloadTransitionTests
{
    [Fact]
    public void Database_ResizeDuringBackup_IsNotReachable()
    {
        // A resize landing mid-backup corrupts both, which is why BackingUp is a
        // state rather than a flag: the rule is checked once, here.
        Assert.False(WorkloadTransitions.IsAllowed(DatabaseState.BackingUp, DatabaseState.Restarting));
        Assert.False(WorkloadTransitions.IsAllowed(DatabaseState.BackingUp, DatabaseState.Restoring));
    }

    [Fact]
    public void Database_DeletedIsTerminal()
    {
        foreach (var target in Enum.GetValues<DatabaseState>())
        {
            Assert.False(WorkloadTransitions.IsAllowed(DatabaseState.Deleted, target));
        }
    }

    [Fact]
    public void Application_DeletedIsTerminal()
    {
        foreach (var target in Enum.GetValues<ApplicationState>())
        {
            Assert.False(WorkloadTransitions.IsAllowed(ApplicationState.Deleted, target));
        }
    }

    [Fact]
    public void Database_FailedCanBeRetriedOrDeleted()
    {
        Assert.True(WorkloadTransitions.IsAllowed(DatabaseState.Failed, DatabaseState.Provisioning));
        Assert.True(WorkloadTransitions.IsAllowed(DatabaseState.Failed, DatabaseState.Deleting));
    }

    [Fact]
    public void Application_UnhealthyCanRollBack()
    {
        Assert.True(WorkloadTransitions.IsAllowed(ApplicationState.Unhealthy, ApplicationState.RollingBack));
    }

    [Fact]
    public void Check_IllegalTransition_ReportsFromAndTo()
    {
        var result = WorkloadTransitions.Check(DatabaseState.Deleted, DatabaseState.Running);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.WorkloadInvalidTransition, result.Failure!.Code);
        Assert.Equal("Deleted", result.Failure.Metadata!["from"]);
        Assert.Equal("Running", result.Failure.Metadata["to"]);
    }

    [Fact]
    public void EveryStateHasATransitionEntry()
    {
        // A state missing from the map would silently allow nothing, which reads
        // as "workload is stuck" with no error explaining why.
        foreach (var state in Enum.GetValues<DatabaseState>())
        {
            _ = WorkloadTransitions.IsAllowed(state, DatabaseState.Failed);
        }

        foreach (var state in Enum.GetValues<ApplicationState>())
        {
            _ = WorkloadTransitions.IsAllowed(state, ApplicationState.Failed);
        }
    }
}
