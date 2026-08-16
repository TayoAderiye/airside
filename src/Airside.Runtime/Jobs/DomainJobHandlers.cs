using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Jobs;
using Airside.Core.Naming;
using Airside.Core.Proxy;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Jobs;

public static class DomainJobTypes
{
    public const string Bind = "domain.bind";
    public const string Unbind = "domain.unbind";
}

/// <param name="Hostname">
/// Carried in the payload rather than looked up, because unbinding soft-deletes
/// the row before the job runs. Reading it back through the store returns
/// nothing, and the handler used to treat that as "already gone" and report
/// success without touching the proxy — so a domain the operator had just
/// removed went on serving traffic until reconciliation noticed, up to two
/// minutes later.
/// </param>
public sealed record DomainPayload(Guid DomainId, bool Bind, string? Hostname = null);

/// <summary>Everything binding a domain needs, resolved before the job runs.</summary>
public sealed record DomainTarget(
    Guid DomainId,
    string Hostname,
    Guid ApplicationId,
    string ApplicationSlug,
    string ApplicationNetworkName,
    string? CurrentContainerName,
    int ContainerPort);

public interface IDomainStore
{
    Task<DomainTarget?> GetAsync(Guid domainId, CancellationToken ct);

    Task RecordBoundAsync(Guid domainId, string routeId, CancellationToken ct);

    Task RecordFailedAsync(Guid domainId, string code, CancellationToken ct);

    /// <summary>Every live domain, for reconciling the proxy back to the database.</summary>
    Task<IReadOnlyList<DomainTarget>> ListLiveAsync(CancellationToken ct);
}

/// <summary>
/// Points a hostname at an application.
/// </summary>
/// <remarks>
/// Two things have to happen and the order matters. The proxy must first join the
/// application's own network, or the upstream it is told about resolves to
/// nothing — Airside's isolation is pairwise, so Caddy has no route to an
/// application it has not been attached to. Only then is the route registered,
/// which is also what triggers Caddy to obtain a certificate.
/// </remarks>
public sealed class BindDomainHandler(
    IProxyManager proxy,
    IContainerRuntime runtime,
    IDomainStore store,
    ILogger<BindDomainHandler> logger) : IJobHandler
{
    public string JobType => DomainJobTypes.Bind;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<DomainPayload>();
        var target = await store.GetAsync(payload.DomainId, ct).ConfigureAwait(false);

        if (target is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The domain no longer exists.");
        }

        if (target.CurrentContainerName is null)
        {
            // Recorded but not routed. Sending traffic to an application that has
            // never deployed would produce a 502 and, worse, would ask Let's
            // Encrypt for a certificate for something that cannot serve the
            // challenge — repeatedly, into a rate limit.
            await store.RecordFailedAsync(payload.DomainId, "domain.application_not_deployed", ct)
                .ConfigureAwait(false);

            return new Error(
                "domain.application_not_deployed",
                "This application has not been deployed yet, so there is nothing to route to. Deploy it "
                + "first; the domain is saved and will be routed automatically.");
        }

        await context.ReportProgressAsync(25, "Attaching the proxy to the application network", ct)
            .ConfigureAwait(false);

        await EnsureProxyOnNetworkAsync(context, target, ct).ConfigureAwait(false);

        await context.ReportProgressAsync(60, "Registering the route", ct).ConfigureAwait(false);

        await proxy.UpsertRouteAsync(
            new RouteSpec(target.Hostname, new UpstreamTarget(target.CurrentContainerName, target.ContainerPort)),
            ct).ConfigureAwait(false);

        await context.TrackResourceAsync(
            JobResourceKind.ProxyRoute, target.Hostname, true, ct).ConfigureAwait(false);

        await store.RecordBoundAsync(payload.DomainId, Proxy.CaddyProxyManager.RouteId(target.Hostname), ct)
            .ConfigureAwait(false);

        await context.LogStepAsync(
            "route",
            $"{target.Hostname} now routes to {target.CurrentContainerName}:{target.ContainerPort}. "
            + "Caddy will obtain a certificate once DNS points here.", ct).ConfigureAwait(false);

        await context.ReportProgressAsync(100, "Routed", ct).ConfigureAwait(false);

        return Result.Ok();
    }

    /// <summary>
    /// Connects the proxy container to an application's network.
    /// </summary>
    /// <remarks>
    /// Caddy joins every application network rather than every application
    /// sharing one. That is what makes isolation pairwise: two applications never
    /// share a network, so one cannot reach the other, and only the proxy spans
    /// them.
    /// </remarks>
    private async Task EnsureProxyOnNetworkAsync(
        IJobContext context,
        DomainTarget target,
        CancellationToken ct)
    {
        var proxyContainer = await runtime.Containers
            .FindAsync(AirsideLabels.SystemContainers.Proxy, ct)
            .ConfigureAwait(false);

        if (proxyContainer is null)
        {
            throw new Proxy.ProxyUnavailableException(
                $"The {AirsideLabels.SystemContainers.Proxy} container is not running, so no domain can be "
                + "routed.");
        }

        if (proxyContainer.Networks.Contains(target.ApplicationNetworkName, StringComparer.Ordinal))
        {
            return;
        }

        await runtime.Networks
            .ConnectAsync(target.ApplicationNetworkName, proxyContainer.Id, ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Attached the proxy to {Network} so it can reach {Application}",
            target.ApplicationNetworkName, target.ApplicationSlug);

        await context.LogStepAsync(
            "network", $"Proxy joined {target.ApplicationNetworkName}.", ct).ConfigureAwait(false);
    }

    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var resource in (await context.GetTrackedResourcesAsync(ct).ConfigureAwait(false)).Reverse())
        {
            if (resource.Kind == JobResourceKind.ProxyRoute && resource.CreatedByThisJob)
            {
                // A half-registered route would send traffic somewhere the record
                // does not claim it goes.
                await proxy.RemoveRouteAsync(resource.Reference, ct).ConfigureAwait(false);
                await context.LogStepAsync("compensate", $"Withdrew the route for {resource.Reference}.", ct)
                    .ConfigureAwait(false);
            }
        }

        var payload = context.GetPayload<DomainPayload>();
        await store.RecordFailedAsync(payload.DomainId, "domain.bind_failed", ct).ConfigureAwait(false);
    }
}

/// <summary>Withdraws a hostname's route.</summary>
public sealed class UnbindDomainHandler(IProxyManager proxy, IDomainStore store) : IJobHandler
{
    public string JobType => DomainJobTypes.Unbind;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<DomainPayload>();

        // Removal is by hostname, which is all the route id derives from, so the
        // domain row does not need to still be readable. Falling back to the
        // store covers a job enqueued before the payload carried the hostname.
        var hostname = payload.Hostname
            ?? (await store.GetAsync(payload.DomainId, ct).ConfigureAwait(false))?.Hostname;

        if (hostname is not null)
        {
            await proxy.RemoveRouteAsync(hostname, ct).ConfigureAwait(false);
            await context.LogStepAsync("route", $"Withdrew the route for {hostname}.", ct)
                .ConfigureAwait(false);
        }

        // The proxy is deliberately left attached to the application's network.
        // Another domain may still route to it, and detaching would break that
        // one to tidy up this one.
        await context.ReportProgressAsync(100, "Unrouted", ct).ConfigureAwait(false);

        return Result.Ok();
    }

    public Task CompensateAsync(IJobContext context, CancellationToken ct) =>
        // Nothing was created. A failed withdrawal leaves the route in place,
        // which reconciliation corrects on its next pass.
        Task.CompletedTask;
}
