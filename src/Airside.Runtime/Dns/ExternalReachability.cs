using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Airside.Core.Containers;
using Airside.Core.Domains;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Airside.Runtime.Dns;

public sealed class ReachabilityOptions
{
    public const string Section = "Airside:Reachability";

    /// <summary>
    /// Set this and Airside never asks anyone what its address is.
    /// </summary>
    /// <remarks>
    /// The correct setting behind NAT, on a provider that hands out a floating
    /// IP, and for anyone who would rather their control plane not make outbound
    /// calls at all.
    /// </remarks>
    public string? PublicAddressOverride { get; set; }

    /// <summary>
    /// Services asked for the host's public address.
    /// </summary>
    /// <remarks>
    /// This is an outbound call from a self-hosted tool, so it is worth being
    /// plain about it: without the host's public address there is nothing to
    /// compare a DNS answer against, and the single most valuable pre-flight check
    /// cannot run. Set <see cref="PublicAddressOverride"/> to switch it off.
    /// </remarks>
    public IList<string> PublicAddressEndpoints { get; } =
        ["https://api.ipify.org", "https://icanhazip.com"];

    /// <summary>
    /// An outside service that reports whether a TCP port is reachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty by default, and deliberately so. Whether the internet can reach port
    /// 80 is genuinely unanswerable from inside the host — a socket bound to 80
    /// tells you nothing about a security group in front of it — but there is no
    /// universal, dependency-free service to ask, and hard-coding somebody's
    /// endpoint into a self-hosted tool is not a decision to make on a user's
    /// behalf.
    /// </para>
    /// <para>
    /// So without it the port checks report <see cref="PreflightSeverity.Unknown"/>
    /// and say plainly that a firewall or security group is the likeliest cause of
    /// a challenge failure. That is more honest than a green tick from a local
    /// bind check that proves nothing about reachability.
    /// </para>
    /// </remarks>
    public string? PortProbeEndpoint { get; set; }

    public int TimeoutSeconds { get; set; } = 6;
}

/// <inheritdoc />
public sealed class ExternalReachability(
    HttpClient http,
    IContainerRuntime runtime,
    IOptions<ReachabilityOptions> options,
    ILogger<ExternalReachability> logger) : IExternalReachability
{
    private IPAddress? _cached;

    public async Task<IPAddress?> GetPublicAddressAsync(CancellationToken ct)
    {
        var settings = options.Value;

        if (!string.IsNullOrWhiteSpace(settings.PublicAddressOverride))
        {
            return IPAddress.TryParse(settings.PublicAddressOverride.Trim(), out var configured)
                ? configured
                : null;
        }

        if (_cached is not null)
        {
            return _cached;
        }

        foreach (var endpoint in settings.PublicAddressEndpoints)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

                var body = await http.GetStringAsync(endpoint, cts.Token).ConfigureAwait(false);

                if (IPAddress.TryParse(body.Trim(), out var address))
                {
                    _cached = address;
                    return address;
                }
            }
#pragma warning disable CA1031 // Any failure means the same thing: try the next one, then give up.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogDebug(ex, "Could not read the public address from {Endpoint}", endpoint);
            }
        }

        // Reported as unknown by the caller. Guessing would be worse than saying
        // so: a wrong address turns every correct DNS record into a failure.
        return null;
    }

    public async Task<PortProbe> ProbeAsync(string hostname, int port, CancellationToken ct)
    {
        var endpoint = options.Value.PortProbeEndpoint;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new PortProbe(port, null,
                "No external port probe is configured, so reachability from the internet could not be "
                + "checked. A cloud firewall or security group is invisible from inside this host.");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));

            var url = $"{endpoint.TrimEnd('/')}?host={Uri.EscapeDataString(hostname)}&port={port}";
            using var response = await http.GetAsync(new Uri(url), cts.Token).ConfigureAwait(false);

            return new PortProbe(port, response.IsSuccessStatusCode);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogDebug(ex, "External port probe for {Host}:{Port} failed", hostname, port);
            return new PortProbe(port, null, "The external port probe could not be reached.");
        }
    }

    /// <summary>
    /// Asks Docker which container publishes the port.
    /// </summary>
    /// <remarks>
    /// Not a local socket connect, which would be misleading: the API runs in its
    /// own network namespace, so <c>127.0.0.1:80</c> is the API container's
    /// loopback and says nothing about the host's. Docker's port bindings are the
    /// one view of the host's ports available from in here.
    /// </remarks>
    public async Task<LocalPortHolder?> WhoHoldsAsync(int port, CancellationToken ct)
    {
        try
        {
            var containers = await runtime.Containers
                .ListManagedAsync(null, ct)
                .ConfigureAwait(false);

            foreach (var container in containers)
            {
                if (container.Ports.Any(p => p.HostPort == port))
                {
                    return new LocalPortHolder(
                        port,
                        container.Name,
                        IsAirsideProxy: string.Equals(
                            container.Name,
                            Core.Naming.AirsideLabels.SystemContainers.Proxy,
                            StringComparison.Ordinal));
                }
            }

            return null;
        }
#pragma warning disable CA1031 // A runtime that will not answer must not fail the whole pre-flight.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogDebug(ex, "Could not determine what holds port {Port}", port);
            return null;
        }
    }

    /// <summary>
    /// Opens a TCP connection to an address to see whether this host can reach it.
    /// </summary>
    /// <remarks>
    /// Used for the IPv6 trap. It is a local probe rather than an outside one, so
    /// it needs no third party, and it answers the question that matters: whether
    /// this machine has working IPv6 at all. A host with an AAAA record it cannot
    /// itself route is one where ACME validation will fail while the A record
    /// looks perfect.
    /// </remarks>
    public static async Task<bool> CanOpenAsync(IPAddress address, int port, TimeSpan timeout)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(timeout);

            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// Cloudflare's published edge ranges, embedded.
/// </summary>
/// <remarks>
/// An A record pointing into these means the name is proxied — orange-clouded —
/// so the address is a CDN edge and not this host. HTTP-01 challenges are
/// intercepted there, and the resulting failure looks identical to a
/// misconfigured A record unless it is named for what it is.
/// </remarks>
public sealed class ProxyRangeIndex
{
    private const string ResourceName = "Airside.Runtime.Dns.data.cloudflare_ranges.txt";

    private readonly List<(IPAddress Network, int Prefix)> _ranges = [];

    public ProxyRangeIndex()
    {
        using var stream = typeof(ProxyRangeIndex).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            return;
        }

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            var entry = line.Trim();

            if (entry.Length == 0 || entry.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = entry.Split('/');

            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var network)
                && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var prefix))
            {
                _ranges.Add((network, prefix));
            }
        }
    }

    public bool IsKnownProxy(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return _ranges.Any(r => r.Network.AddressFamily == address.AddressFamily
            && Contains(r.Network, r.Prefix, address));
    }

    private static bool Contains(IPAddress network, int prefix, IPAddress address)
    {
        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();

        if (networkBytes.Length != addressBytes.Length)
        {
            return false;
        }

        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));

        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }
}
