using Airside.Core.Common;

namespace Airside.Core.Workloads;

public enum WorkloadKind
{
    Database,
    Application,
}

public enum DatabaseState
{
    Provisioning,
    Running,
    Stopped,
    Restarting,
    BackingUp,
    Restoring,
    Failed,
    Deleting,
    Deleted,
}

public enum ApplicationState
{
    Created,
    Building,
    Deploying,
    Running,
    Stopped,
    Unhealthy,
    Failed,
    RollingBack,
    Deleting,
    Deleted,
}

/// <summary>
/// The legal transitions, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <c>BackingUp</c> is a state rather than a flag because a backup must block a
/// resize, and a resize landing mid-backup corrupts both. Expressed as a
/// transition table the rule is checked once and tested once; expressed as a flag
/// it becomes a conditional that somebody eventually forgets.
/// </para>
/// <para>
/// An illegal transition is a <c>409</c> carrying
/// <c>workload.invalid_transition</c>, never a silent write.
/// </para>
/// </remarks>
public static class WorkloadTransitions
{
    private static readonly Dictionary<DatabaseState, DatabaseState[]> DatabaseMap = new()
    {
        [DatabaseState.Provisioning] = [DatabaseState.Running, DatabaseState.Failed, DatabaseState.Deleting],
        [DatabaseState.Running] = [DatabaseState.Stopped, DatabaseState.Restarting, DatabaseState.BackingUp, DatabaseState.Restoring, DatabaseState.Failed, DatabaseState.Deleting],
        [DatabaseState.Stopped] = [DatabaseState.Running, DatabaseState.Restoring, DatabaseState.Deleting],
        [DatabaseState.Restarting] = [DatabaseState.Running, DatabaseState.Failed],
        [DatabaseState.BackingUp] = [DatabaseState.Running, DatabaseState.Failed],
        [DatabaseState.Restoring] = [DatabaseState.Running, DatabaseState.Stopped, DatabaseState.Failed],
        [DatabaseState.Failed] = [DatabaseState.Provisioning, DatabaseState.Restarting, DatabaseState.Deleting],
        [DatabaseState.Deleting] = [DatabaseState.Deleted, DatabaseState.Failed],
        [DatabaseState.Deleted] = [],
    };

    private static readonly Dictionary<ApplicationState, ApplicationState[]> ApplicationMap = new()
    {
        [ApplicationState.Created] = [ApplicationState.Building, ApplicationState.Deleting],
        [ApplicationState.Building] = [ApplicationState.Deploying, ApplicationState.Failed, ApplicationState.Deleting],
        [ApplicationState.Deploying] = [ApplicationState.Running, ApplicationState.Failed],
        [ApplicationState.Running] = [ApplicationState.Stopped, ApplicationState.Unhealthy, ApplicationState.Building, ApplicationState.RollingBack, ApplicationState.Deleting],
        [ApplicationState.Stopped] = [ApplicationState.Running, ApplicationState.Building, ApplicationState.Deleting],
        [ApplicationState.Unhealthy] = [ApplicationState.Running, ApplicationState.Failed, ApplicationState.RollingBack, ApplicationState.Stopped],
        [ApplicationState.Failed] = [ApplicationState.Building, ApplicationState.RollingBack, ApplicationState.Deleting],
        [ApplicationState.RollingBack] = [ApplicationState.Running, ApplicationState.Failed],
        [ApplicationState.Deleting] = [ApplicationState.Deleted, ApplicationState.Failed],
        [ApplicationState.Deleted] = [],
    };

    public static bool IsAllowed(DatabaseState from, DatabaseState to) =>
        DatabaseMap.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0;

    public static bool IsAllowed(ApplicationState from, ApplicationState to) =>
        ApplicationMap.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0;

    public static Result Check(DatabaseState from, DatabaseState to) =>
        IsAllowed(from, to) ? Result.Ok() : InvalidTransition(from.ToString(), to.ToString());

    public static Result Check(ApplicationState from, ApplicationState to) =>
        IsAllowed(from, to) ? Result.Ok() : InvalidTransition(from.ToString(), to.ToString());

    private static Error InvalidTransition(string from, string to) => new(
        ErrorCodes.WorkloadInvalidTransition,
        $"A workload in state {from} cannot move to {to}.",
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["from"] = from,
            ["to"] = to,
        });
}
