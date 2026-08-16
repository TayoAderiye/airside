using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Jobs;
using Airside.Core.Proxy;
using Airside.Core.Workloads;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Jobs;

public sealed record ApplicationLifecyclePayload(Guid ApplicationId);

/// <param name="ReleaseDomains">
/// Withdraws the routes rather than refusing the delete. Required, with no
/// default, because the two outcomes are very different for anyone visiting the
/// site.
/// </param>
public sealed record ApplicationDeletePayload(
    Guid ApplicationId,
    bool DeleteVolumes,
    bool ReleaseDomains);

/// <summary>What a delete needs, resolved before the containers are gone.</summary>
public sealed record ApplicationTeardown(
    Guid ApplicationId,
    Slug Slug,
    string? ContainerId,
    string NetworkName,
    IReadOnlyList<string> Hostnames,
    IReadOnlyList<string> VolumeNames,
    IReadOnlyList<string> ImageIds);

public interface IApplicationLifecycleStore
{
    Task<ApplicationTeardown?> GetTeardownAsync(Guid applicationId, CancellationToken ct);

    Task<string?> GetContainerIdAsync(Guid applicationId, CancellationToken ct);

    Task SetLifecycleStateAsync(Guid applicationId, string state, CancellationToken ct);

    Task MarkDeletedAsync(Guid applicationId, CancellationToken ct);

    /// <summary>Withdraws every domain bound to the application, in one transaction.</summary>
    Task ReleaseDomainsAsync(Guid applicationId, CancellationToken ct);
}

/// <summary>
/// Start, stop, and restart for an application.
/// </summary>
/// <remarks>
/// Stopping is not the same as deleting, and the difference has to be visible to
/// visitors: the hostname stays routed, so the proxy is switched to a holding
/// page rather than left pointing at a container that is no longer answering. A
/// bare 502 tells a visitor nothing and an operator almost nothing.
/// </remarks>
public sealed class ApplicationLifecycleHandler(
    IContainerRuntime runtime,
    IProxyManager proxy,
    IApplicationLifecycleStore store,
    IDomainStore domains,
    string jobType,
    ApplicationState targetState,
    ILogger<ApplicationLifecycleHandler> logger) : IJobHandler
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public string JobType => jobType;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<ApplicationLifecyclePayload>();
        var containerId = await store.GetContainerIdAsync(payload.ApplicationId, ct).ConfigureAwait(false);

        if (containerId is null)
        {
            return new Error(
                ErrorCodes.WorkloadNotFound,
                "The application has no container to act on. Deploy it first.");
        }

        await context.ReportProgressAsync(30, jobType, ct).ConfigureAwait(false);

        switch (jobType)
        {
            case ApplicationJobTypes.Start:
                await runtime.Containers.StartAsync(containerId, ct).ConfigureAwait(false);
                break;

            case ApplicationJobTypes.Stop:
                await runtime.Containers.StopAsync(containerId, StopTimeout, ct).ConfigureAwait(false);
                break;

            case ApplicationJobTypes.Restart:
                await runtime.Containers.RestartAsync(containerId, StopTimeout, ct).ConfigureAwait(false);
                break;

            default:
                return new Error("job.no_handler", $"'{jobType}' is not a lifecycle operation.");
        }

        await store.SetLifecycleStateAsync(payload.ApplicationId, targetState.ToString(), ct)
            .ConfigureAwait(false);

        await SwitchRoutesAsync(payload.ApplicationId, maintenance: targetState != ApplicationState.Running, ct)
            .ConfigureAwait(false);

        await context.ReportProgressAsync(100, targetState.ToString(), ct).ConfigureAwait(false);

        return Result.Ok();
    }

    /// <summary>
    /// Points every hostname at a holding page, or back at the container.
    /// </summary>
    /// <remarks>
    /// Best effort on purpose. The container has already stopped by this point, so
    /// failing the job because the proxy would not answer would report a stop that
    /// did happen as a failure — and reconciliation puts the routes right on its
    /// next pass regardless.
    /// </remarks>
    private async Task SwitchRoutesAsync(Guid applicationId, bool maintenance, CancellationToken ct)
    {
        try
        {
            var live = await domains.ListLiveAsync(ct).ConfigureAwait(false);

            foreach (var domain in live.Where(d =>
                d.ApplicationId == applicationId && d.CurrentContainerName is not null))
            {
                await proxy.UpsertRouteAsync(
                    new RouteSpec(
                        domain.Hostname,
                        new UpstreamTarget(domain.CurrentContainerName!, domain.ContainerPort),
                        domain.TlsMode,
                        domain.Hsts,
                        domain.RedirectTo,
                        maintenance),
                    ct).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The lifecycle change already happened; reconciliation will settle the routes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(
                ex, "Could not switch the routes for {ApplicationId} after a {JobType}", applicationId, jobType);
        }
    }

    /// <summary>
    /// Nothing was created, so there is nothing to unwind — but the state must not
    /// be left mid-transition.
    /// </summary>
    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<ApplicationLifecyclePayload>();

        await store.SetLifecycleStateAsync(payload.ApplicationId, nameof(ApplicationState.Failed), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Removes an application and everything Airside created for it.
/// </summary>
/// <remarks>
/// <para>
/// The order is deliberate: routes first, then the container, then the network,
/// then optionally the volumes. Withdrawing routes before stopping the container
/// means no visitor ever sees a 502 from a proxy pointing at something that has
/// gone — the hostname simply stops answering, which is what a deletion means.
/// </para>
/// <para>
/// Volumes are kept unless the caller explicitly asked otherwise, on the same
/// principle as database deletion: an application's data outliving a mistaken
/// delete is recoverable, and the reverse is not.
/// </para>
/// </remarks>
public sealed class ApplicationDeleteHandler(
    IContainerRuntime runtime,
    IProxyManager proxy,
    IApplicationLifecycleStore store,
    ILogger<ApplicationDeleteHandler> logger) : IJobHandler
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public string JobType => ApplicationJobTypes.Delete;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<ApplicationDeletePayload>();
        var teardown = await store.GetTeardownAsync(payload.ApplicationId, ct).ConfigureAwait(false);

        if (teardown is null)
        {
            // Already gone. Deleting something twice is not an error.
            return Result.Ok();
        }

        // First, so a hostname stops answering rather than answering with a
        // gateway error from a proxy aimed at a container that is being removed.
        await context.ReportProgressAsync(10, "Withdrawing routes", ct).ConfigureAwait(false);

        foreach (var hostname in teardown.Hostnames)
        {
            await proxy.RemoveRouteAsync(hostname, ct).ConfigureAwait(false);
            await proxy.UnloadCertificateAsync(hostname, ct).ConfigureAwait(false);
        }

        if (teardown.Hostnames.Count > 0)
        {
            await store.ReleaseDomainsAsync(payload.ApplicationId, ct).ConfigureAwait(false);

            await context.LogStepAsync(
                "domains",
                $"Released {teardown.Hostnames.Count} domain(s): {string.Join(", ", teardown.Hostnames)}. "
                + "They can be attached to another application.",
                ct).ConfigureAwait(false);
        }

        if (teardown.ContainerId is not null)
        {
            await context.ReportProgressAsync(35, "Stopping the container", ct).ConfigureAwait(false);

            await SwallowAsync(
                () => runtime.Containers.StopAsync(teardown.ContainerId, StopTimeout, ct),
                "stop the container");

            await SwallowAsync(
                () => runtime.Containers.RemoveAsync(teardown.ContainerId, force: true, ct),
                "remove the container");
        }

        await context.ReportProgressAsync(60, "Removing the network", ct).ConfigureAwait(false);

        await SwallowAsync(
            () => runtime.Networks.RemoveAsync(teardown.NetworkName, ct),
            $"remove the network {teardown.NetworkName}");

        if (payload.DeleteVolumes)
        {
            await context.ReportProgressAsync(80, "Removing volumes", ct).ConfigureAwait(false);

            foreach (var volume in teardown.VolumeNames)
            {
                await SwallowAsync(
                    () => runtime.Volumes.RemoveAsync(volume, force: true, ct),
                    $"remove the volume {volume}");
            }
        }
        else if (teardown.VolumeNames.Count > 0)
        {
            // Said out loud rather than left for someone to discover in `docker
            // volume ls` months later, wondering what it belonged to.
            await context.LogStepAsync(
                "volumes",
                $"Kept {teardown.VolumeNames.Count} volume(s): {string.Join(", ", teardown.VolumeNames)}. "
                + "They are no longer attached to anything and will not be reused.",
                ct).ConfigureAwait(false);
        }

        await store.MarkDeletedAsync(payload.ApplicationId, ct).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Deleted", ct).ConfigureAwait(false);

        logger.LogInformation("Deleted application {Slug}", teardown.Slug.Value);

        return Result.Ok();
    }

    /// <summary>
    /// A half-finished delete is finished, not reversed.
    /// </summary>
    /// <remarks>
    /// There is nothing to restore — the container and network are gone. Marking
    /// the row deleted is the honest outcome, and leaves reconciliation to report
    /// anything that survived rather than leaving a workload nobody can act on.
    /// </remarks>
    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<ApplicationDeletePayload>();

        await store.MarkDeletedAsync(payload.ApplicationId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A teardown step that fails must not stop the rest.
    /// </summary>
    /// <remarks>
    /// Leaving the network behind because the container would not stop would leave
    /// the application half-deleted and undeletable, which is worse than a leaked
    /// object that reconciliation will report.
    /// </remarks>
    private async Task SwallowAsync(Func<Task> action, string description)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "Could not {Description} during delete; continuing", description);
        }
    }
}
