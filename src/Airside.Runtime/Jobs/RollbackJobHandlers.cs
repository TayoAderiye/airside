using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Jobs;
using Airside.Core.Workloads;

namespace Airside.Runtime.Jobs;

/// <summary>
/// Joins or removes an application from a database's network.
/// </summary>
/// <remarks>
/// This is the enforcement half of an attachment. The row records the
/// authorisation; this makes it real, and detaching removes the route so an
/// application can no longer resolve a database it is no longer attached to.
/// Both are live Docker operations needing no restart — though the injected
/// environment only changes on the next deploy, which the API says explicitly.
/// </remarks>
public sealed class AttachmentHandler(
    IContainerRuntime runtime,
    IApplicationStore store) : IJobHandler
{
    public string JobType => ApplicationJobTypes.AttachDatabase;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<AttachmentPayload>();
        var target = await store.GetAttachmentAsync(payload.AttachmentId, ct).ConfigureAwait(false);

        if (target is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The attachment no longer exists.");
        }

        if (target.ApplicationContainerId is null)
        {
            // Nothing running yet. The attachment still stands; the next deploy
            // joins the network as part of starting the container.
            await store.RecordAttachmentAppliedAsync(payload.AttachmentId, ct).ConfigureAwait(false);
            await context.LogStepAsync(
                "attach",
                "Recorded. The application is not running, so the network is joined at its next deploy.",
                ct).ConfigureAwait(false);

            return Result.Ok();
        }

        if (payload.Attach)
        {
            await runtime.Networks
                .ConnectAsync(target.DatabaseNetworkName, target.ApplicationContainerId, ct)
                .ConfigureAwait(false);

            await context.TrackResourceAsync(
                JobResourceKind.Network, target.DatabaseNetworkName, false, ct).ConfigureAwait(false);

            await context.LogStepAsync(
                "attach",
                "Joined the database network. Injected environment variables appear on the next deploy.",
                ct).ConfigureAwait(false);
        }
        else
        {
            await runtime.Networks
                .DisconnectAsync(target.DatabaseNetworkName, target.ApplicationContainerId, ct)
                .ConfigureAwait(false);

            await context.LogStepAsync("detach", "Left the database network.", ct).ConfigureAwait(false);
        }

        await store.RecordAttachmentAppliedAsync(payload.AttachmentId, ct).ConfigureAwait(false);
        await context.ReportProgressAsync(100, payload.Attach ? "Attached" : "Detached", ct).ConfigureAwait(false);

        return Result.Ok();
    }

    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<AttachmentPayload>();

        if (!payload.Attach)
        {
            return;
        }

        // An attach that failed halfway must not leave the container on a network
        // the authorisation record does not reflect — that is a database reachable
        // by an application nobody believes is attached to it.
        var target = await store.GetAttachmentAsync(payload.AttachmentId, ct).ConfigureAwait(false);

        if (target?.ApplicationContainerId is not null)
        {
            await runtime.Networks
                .DisconnectAsync(target.DatabaseNetworkName, target.ApplicationContainerId, ct)
                .ConfigureAwait(false);

            await context.LogStepAsync(
                "compensate", "Removed the network join so access matches the record.", ct).ConfigureAwait(false);
        }
    }
}
