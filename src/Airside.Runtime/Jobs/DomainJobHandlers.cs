using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Domains;
using Airside.Core.Jobs;
using Airside.Core.Proxy;
using Airside.Core.Security;
using Airside.Runtime.Proxy;
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
    int ContainerPort,
    TlsMode TlsMode,
    Guid? CertificateSecretId = null,
    HstsPolicy? Hsts = null,
    string? RedirectTo = null,
    bool ApplicationIsRunning = true);

public interface IDomainStore
{
    Task<DomainTarget?> GetAsync(Guid domainId, CancellationToken ct);

    Task RecordStatusAsync(Guid domainId, DomainStatus status, CancellationToken ct);

    Task RecordBoundAsync(Guid domainId, string routeId, CancellationToken ct);

    Task RecordFailedAsync(Guid domainId, string code, string? message, CancellationToken ct);

    /// <summary>Every live domain, for reconciling the proxy back to the database.</summary>
    Task<IReadOnlyList<DomainTarget>> ListLiveAsync(CancellationToken ct);

    /// <summary>Hostnames Caddy must not attempt automatic HTTPS for — everything not Automatic.</summary>
    Task<IReadOnlyList<string>> ListAutomaticHttpsSkipAsync(CancellationToken ct);

    /// <summary>Reads back a stored certificate for loading into the proxy.</summary>
    Task<ManualCertificate?> GetManualCertificateAsync(Guid domainId, CancellationToken ct);
}

/// <summary>
/// Serialises work on one hostname.
/// </summary>
/// <remarks>
/// Two operations on the same hostname interleaving produces a route that matches
/// neither request — an attach registering its route after a concurrent detach has
/// removed one, for instance, leaves traffic flowing to a domain the operator
/// believes is gone. The job queue already serialises per workload, but a hostname
/// can move between applications, so the lock is keyed on the name.
/// </remarks>
public sealed class HostnameLocks
{
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string hostname, CancellationToken ct)
    {
        SemaphoreSlim semaphore;

        lock (_locks)
        {
            if (!_locks.TryGetValue(hostname, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _locks[hostname] = semaphore;
            }
        }

        await semaphore.WaitAsync(ct).ConfigureAwait(false);

        return new Release(semaphore);
    }

    private sealed class Release(SemaphoreSlim semaphore) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            semaphore.Release();
        }
    }
}

/// <summary>
/// Points a hostname at an application.
/// </summary>
/// <remarks>
/// <para>
/// The order matters. The proxy must first join the application's own network, or
/// the upstream it is told about resolves to nothing — Airside's isolation is
/// pairwise, so Caddy has no route to an application it has not been attached to.
/// </para>
/// <para>
/// Pre-flight runs before any of it for <see cref="TlsMode.Automatic"/>. Letting
/// Caddy attempt a challenge that cannot succeed costs the user a failed-validation
/// slot they will need later, and produces an error naming none of the causes.
/// </para>
/// </remarks>
public sealed class BindDomainHandler(
    IContainerRuntime runtime,
    IProxyManager proxy,
    IDomainStore store,
    IDomainPreflight preflight,
    IIssuanceLedger ledger,
    IPublicSuffixList suffixes,
    ISecretProtector secrets,
    HostnameLocks locks,
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
            return Result.Fail(new Error(ErrorCodes.WorkloadNotFound, "The domain no longer exists."));
        }

        using var _ = await locks.AcquireAsync(target.Hostname, ct).ConfigureAwait(false);

        if (target.CurrentContainerName is null)
        {
            // Registering a route for an application that has never run would give
            // Caddy an upstream that resolves to nothing, and — worse — start an
            // ACME challenge whose failures count against a rate limit the user
            // will want when the deployment does work.
            await store.RecordFailedAsync(
                target.DomainId, "domain.application_not_deployed",
                "The application has not been deployed yet.", ct).ConfigureAwait(false);

            return Result.Fail(new Error(
                "domain.application_not_deployed",
                $"'{target.ApplicationSlug}' has never been deployed, so there is nothing to route to. "
                + "Deploy it first, then add the domain."));
        }

        if (target.TlsMode == TlsMode.Automatic)
        {
            await store.RecordStatusAsync(target.DomainId, DomainStatus.Validating, ct).ConfigureAwait(false);
            await context.ReportProgressAsync(10, "Checking DNS and reachability", ct).ConfigureAwait(false);

            var report = await preflight
                .RunAsync(new PreflightRequest(target.Hostname, target.TlsMode, target.DomainId), ct)
                .ConfigureAwait(false);

            if (report.Blocks)
            {
                var first = report.Blocking.First();

                await store.RecordFailedAsync(target.DomainId, first.Id, first.Summary, ct).ConfigureAwait(false);
                await context.LogStepAsync("preflight", $"{first.Summary} {first.Remedy}", ct).ConfigureAwait(false);

                return Result.Fail(new Error(first.Id, first.Summary, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["found"] = first.Found,
                    ["expected"] = first.Expected,
                    ["remedy"] = first.Remedy,
                }));
            }

            foreach (var warning in report.Checks.Where(c => c.Severity == PreflightSeverity.Warning))
            {
                await context.LogStepAsync("preflight", $"{warning.Summary} {warning.Remedy}", ct)
                    .ConfigureAwait(false);
            }
        }

        await context.ReportProgressAsync(35, "Attaching the proxy to the application network", ct)
            .ConfigureAwait(false);

        await AttachProxyAsync(target, ct).ConfigureAwait(false);

        // Non-Automatic modes must be on the skip list before the route exists, or
        // Caddy starts its own issuance the moment it sees the hostname.
        await ApplySkipListAsync(ct).ConfigureAwait(false);

        if (target.TlsMode == TlsMode.Manual)
        {
            await context.ReportProgressAsync(50, "Loading the certificate", ct).ConfigureAwait(false);
            await LoadManualCertificateAsync(target, ct).ConfigureAwait(false);
        }

        await context.ReportProgressAsync(70, "Registering the route", ct).ConfigureAwait(false);

        var route = new RouteSpec(
            target.Hostname,
            new UpstreamTarget(target.CurrentContainerName, target.ContainerPort),
            target.TlsMode,
            target.Hsts,
            target.RedirectTo,
            Maintenance: !target.ApplicationIsRunning);

        await proxy.UpsertRouteAsync(route, ct).ConfigureAwait(false);
        await store.RecordBoundAsync(target.DomainId, CaddyRouteId(target.Hostname), ct).ConfigureAwait(false);

        if (target.TlsMode == TlsMode.Automatic)
        {
            await store.RecordStatusAsync(target.DomainId, DomainStatus.Issuing, ct).ConfigureAwait(false);

            // Recorded as an attempt whether or not it succeeds, because the
            // authority counts it either way and the whole point of the ledger is
            // to know what has been spent.
            await ledger.RecordAsync(
                new IssuanceAttemptRecord(
                    target.Hostname,
                    suffixes.GetRegisteredDomain(target.Hostname) ?? target.Hostname,
                    Succeeded: true,
                    Staging: false,
                    ErrorCode: null,
                    RetryAfter: null),
                ct).ConfigureAwait(false);

            await context.LogStepAsync(
                "certificate",
                "The route is live. A certificate is requested on the first request to this hostname and "
                + "usually arrives within a minute.",
                ct).ConfigureAwait(false);
        }

        await context.ReportProgressAsync(100, "Routed", ct).ConfigureAwait(false);

        logger.LogInformation(
            "Bound {Hostname} to {Application} in {Mode} mode",
            target.Hostname, target.ApplicationSlug, target.TlsMode);

        return Result.Ok();
    }

    private async Task AttachProxyAsync(DomainTarget target, CancellationToken ct)
    {
        var proxyContainer = await runtime.Containers
            .FindAsync(Core.Naming.AirsideLabels.SystemContainers.Proxy, ct)
            .ConfigureAwait(false);

        if (proxyContainer is null)
        {
            throw new ProxyUnavailableException(
                "The airside-proxy container was not found, so no route can be registered.");
        }

        if (!proxyContainer.Networks.Contains(target.ApplicationNetworkName, StringComparer.Ordinal))
        {
            await runtime.Networks
                .ConnectAsync(target.ApplicationNetworkName, proxyContainer.Id, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task LoadManualCertificateAsync(DomainTarget target, CancellationToken ct)
    {
        var certificate = await store.GetManualCertificateAsync(target.DomainId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"'{target.Hostname}' is set to Manual TLS but has no stored certificate.");

        _ = secrets;

        await proxy.LoadCertificateAsync(certificate, ct).ConfigureAwait(false);
    }

    private async Task ApplySkipListAsync(CancellationToken ct)
    {
        var skip = await store.ListAutomaticHttpsSkipAsync(ct).ConfigureAwait(false);
        await proxy.SetAutomaticHttpsSkipAsync(skip, ct).ConfigureAwait(false);
    }

    private static string CaddyRouteId(string hostname) =>
        CaddyProxyManager.RouteId(hostname);

    /// <summary>
    /// Removes whatever the failed attempt managed to register.
    /// </summary>
    /// <remarks>
    /// A route left behind after a failure points at an upstream nobody agreed to,
    /// and would keep serving until reconciliation noticed. The proxy stays
    /// attached to the application network: another domain may still be using it,
    /// and detaching would break that one to tidy up this one.
    /// </remarks>
    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<DomainPayload>();
        var hostname = payload.Hostname
            ?? (await store.GetAsync(payload.DomainId, ct).ConfigureAwait(false))?.Hostname;

        if (hostname is not null)
        {
            await proxy.RemoveRouteAsync(hostname, ct).ConfigureAwait(false);
        }

        await store.RecordFailedAsync(payload.DomainId, "domain.bind_failed", null, ct).ConfigureAwait(false);
    }
}

/// <summary>Withdraws a hostname's route.</summary>
public sealed class UnbindDomainHandler(
    IProxyManager proxy,
    IDomainStore store,
    HostnameLocks locks) : IJobHandler
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
            using var _ = await locks.AcquireAsync(hostname, ct).ConfigureAwait(false);

            await proxy.RemoveRouteAsync(hostname, ct).ConfigureAwait(false);
            await proxy.UnloadCertificateAsync(hostname, ct).ConfigureAwait(false);

            await context.LogStepAsync("route", $"Withdrew the route for {hostname}.", ct)
                .ConfigureAwait(false);

            // The skip list is rebuilt from what remains, so a hostname that has
            // gone stops being skipped. Leaving it there would silently suppress
            // automatic HTTPS if the same name were added again later.
            var skip = await store.ListAutomaticHttpsSkipAsync(ct).ConfigureAwait(false);
            await proxy.SetAutomaticHttpsSkipAsync(skip, ct).ConfigureAwait(false);
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
