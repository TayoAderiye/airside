namespace Airside.Core.Operations;

/// <summary>
/// The steps an update passes through, in order.
/// </summary>
/// <remarks>
/// Ordered so that the CLI can decide what to do from the step alone: anything at
/// or before <see cref="Swapping"/> can be abandoned by restarting the old image,
/// and anything after it has to go forward or be rolled back explicitly.
/// </remarks>
public enum UpdateStep
{
    Starting,
    BackingUp,
    Pulling,

    /// <summary>The old container has stopped. From here there is no control plane running.</summary>
    Swapping,

    HealthChecking,
    Succeeded,
    RollingBack,
    RolledBack,
    Failed,
}

/// <summary>
/// What to do next, given where an update stopped.
/// </summary>
/// <remarks>
/// <para>
/// The reason <c>state.json</c> exists at all. An operator who finds a host with
/// no control plane running has one question — go forward or go back — and the
/// step is the only thing that answers it. Before the swap nothing has been
/// replaced; after it the old container is already gone, and restarting it is a
/// different operation from finishing the update.
/// </para>
/// <para>
/// A pure function over the step, compiled into both the API and the CLI as
/// linked source. It takes a string rather than the enum because the CLI reads the
/// state file with <c>JsonDocument</c> to stay NativeAOT-clean, and because a
/// state file written by a newer version may name a step this binary does not know
/// — which must produce a message, not a crash.
/// </para>
/// </remarks>
public static class UpdateAdvice
{
    public const string ComposeFile = "/opt/airside/docker-compose.yml";

    public static string For(string step) => step switch
    {
        nameof(UpdateStep.Succeeded) => "The update completed. Nothing to do.",

        nameof(UpdateStep.RolledBack) or nameof(UpdateStep.Failed) =>
            "The update did not complete and the previous version was restored. Nothing to do.",

        // Nothing has been replaced yet, so the running version is whatever was
        // there before — the only risk is that the container is stopped.
        nameof(UpdateStep.Starting) or nameof(UpdateStep.BackingUp) or nameof(UpdateStep.Pulling) =>
            "The update stopped before anything was replaced, so the running version is unchanged.\n"
            + "Start the control plane if it is not running:\n"
            + "  docker start airside-api",

        // The dangerous one: the old container may already be gone, so there is
        // no API and nothing will retry on its own.
        nameof(UpdateStep.Swapping) =>
            "The update stopped while replacing the control plane, so there may be no API running.\n"
            + "Bring the new version up:\n"
            + $"  docker compose -f {ComposeFile} up -d airside-api\n"
            + "If it does not become healthy, roll back:\n"
            + "  airside rollback",

        nameof(UpdateStep.HealthChecking) =>
            "The new version started but had not reported healthy. Check its logs:\n"
            + "  docker logs airside-api\n"
            + "Then either leave it running or roll back with: airside rollback",

        nameof(UpdateStep.RollingBack) =>
            "A rollback was in progress. Re-run it to finish:\n"
            + "  airside rollback",

        _ => "Unrecognised state. Report this with the contents of the state file.",
    };

    /// <summary>
    /// Whether the control plane may be down at this step.
    /// </summary>
    /// <remarks>
    /// Drives whether the CLI's advice leads with "bring it up" or with "nothing
    /// changed", which is the difference between an operator acting immediately
    /// and an operator investigating calmly.
    /// </remarks>
    public static bool MayBeOffline(string step) =>
        step is nameof(UpdateStep.Swapping) or nameof(UpdateStep.HealthChecking) or nameof(UpdateStep.RollingBack);
}
