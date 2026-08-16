using Airside.Core.Containers;
using Airside.Core.Naming;

namespace Airside.Api.Features.Domains;

/// <inheritdoc />
/// <remarks>
/// A one-method seam so the endpoint layer never touches the container runtime
/// directly. The rule that only <c>Airside.Runtime</c> reaches Docker is what
/// makes the security review of that project meaningful, and it stops being true
/// the first time an endpoint takes a shortcut.
/// </remarks>
internal sealed class ProxyNetworkAccessor(IContainerRuntime runtime) : IContainerRuntimeAccessor
{
    public async Task AttachProxyAsync(string networkName, CancellationToken ct)
    {
        var proxy = await runtime.Containers
            .FindAsync(AirsideLabels.SystemContainers.Proxy, ct)
            .ConfigureAwait(false);

        if (proxy is null || proxy.Networks.Contains(networkName, StringComparer.Ordinal))
        {
            return;
        }

        await runtime.Networks.ConnectAsync(networkName, proxy.Id, ct).ConfigureAwait(false);
    }
}
