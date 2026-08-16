using Airside.Core.Proxy;
using Airside.Runtime.Jobs;
using Airside.Tests.Databases;

namespace Airside.Tests.Proxy;

/// <summary>
/// That withdrawing a domain actually withdraws the route, immediately.
/// </summary>
/// <remarks>
/// The endpoint soft-deletes the domain row and then enqueues the job, so by the
/// time the handler runs, reading the domain back returns nothing. The handler
/// read the hostname from the store, found null, and returned success without
/// touching the proxy — the job went green while the hostname carried on serving
/// until reconciliation noticed it, as much as two minutes later.
/// </remarks>
public class DomainUnbindTests
{
    [Fact]
    public async Task UnbindRemovesTheRouteEvenWhenTheDomainRowIsAlreadyGone()
    {
        var proxy = new RecordingProxy();

        // GetAsync returns null, exactly as it does after the soft delete.
        var handler = new UnbindDomainHandler(proxy, new EmptyDomainStore());
        var context = new FakeJobContext(
            new DomainPayload(Guid.CreateVersion7(), Bind: false, "app.example.com"));

        var result = await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("app.example.com", Assert.Single(proxy.Removed));
    }

    [Fact]
    public async Task UnbindWithNoHostnameAnywhereSucceedsWithoutRemovingAnything()
    {
        // A job enqueued before the payload carried a hostname, whose row has
        // since gone. There is nothing left to identify the route by, so
        // reconciliation is the only thing that can clean it up — but the job
        // must not fail, or it would block its workload.
        var proxy = new RecordingProxy();
        var handler = new UnbindDomainHandler(proxy, new EmptyDomainStore());

        var result = await handler.ExecuteAsync(
            new FakeJobContext(new DomainPayload(Guid.CreateVersion7(), Bind: false)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(proxy.Removed);
    }

    private sealed class EmptyDomainStore : IDomainStore
    {
        public Task<DomainTarget?> GetAsync(Guid domainId, CancellationToken ct) =>
            Task.FromResult<DomainTarget?>(null);

        public Task RecordBoundAsync(Guid domainId, string routeId, CancellationToken ct) => Task.CompletedTask;

        public Task RecordFailedAsync(Guid domainId, string code, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<DomainTarget>> ListLiveAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DomainTarget>>([]);
    }

    private sealed class RecordingProxy : IProxyManager
    {
        public List<string> Removed { get; } = [];

        public Task RemoveRouteAsync(string hostname, CancellationToken ct)
        {
            Removed.Add(hostname);
            return Task.CompletedTask;
        }

        public Task UpsertRouteAsync(RouteSpec spec, CancellationToken ct) => Task.CompletedTask;

        public Task SwapUpstreamAsync(string hostname, UpstreamTarget upstream, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RouteSpec>> ListRoutesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteSpec>>([]);

        public Task<CertificateStatus?> GetCertificateAsync(string hostname, CancellationToken ct) =>
            Task.FromResult<CertificateStatus?>(null);

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
