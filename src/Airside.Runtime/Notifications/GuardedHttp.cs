using System.Net;
using System.Net.Sockets;
using Airside.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Notifications;

/// <summary>
/// An HTTP handler that refuses to connect anywhere Airside should not reach.
/// </summary>
/// <remarks>
/// <para>
/// The check happens in <see cref="SocketsHttpHandler.ConnectCallback"/>, at the
/// moment the socket is opened, and the connection is then made to the address
/// that was checked. Validating a URL when it is saved and connecting by hostname
/// later is a hole, not a check: DNS can return a public address while the
/// channel is being configured and <c>127.0.0.1</c> when the webhook actually
/// fires, and nothing in between would notice.
/// </para>
/// <para>
/// Redirects are disabled for the same reason. A remote server answering 302 with
/// <c>Location: http://169.254.169.254/…</c> would otherwise walk the request to
/// the metadata service using a destination that passed every check.
/// </para>
/// </remarks>
public static class GuardedHttp
{
    /// <summary>
    /// Builds the handler used for every outbound notification.
    /// </summary>
    /// <param name="allowPrivateDestinations">
    /// Set only by an operator who has said, in configuration, that they run their
    /// own receiver on the local network. It is off by default because the same
    /// setting is what makes Caddy's admin API reachable from a webhook.
    /// </param>
    public static SocketsHttpHandler CreateHandler(
        bool allowPrivateDestinations,
        ILogger logger,
        TimeSpan? connectTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return new SocketsHttpHandler
        {
            // Never. A redirect is a destination chosen by the remote server, and
            // it has not been through the guard below.
            AllowAutoRedirect = false,

            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),

            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                var addresses = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

                if (addresses.Length == 0)
                {
                    throw new OutboundBlockedException($"'{host}' does not resolve to any address.");
                }

                foreach (var address in addresses)
                {
                    // Not a bypass. Loopback and link-local remain refused however
                    // this is set — see OutboundGuard for why they are not behind
                    // the same switch as a private network.
                    var verdict = OutboundGuard.Check(address, allowPrivateDestinations);

                    if (!verdict.IsAllowed)
                    {
                        // Every resolved address must pass, not just the one that
                        // happens to be tried first. A name resolving to one public
                        // and one private address would otherwise be usable by
                        // retrying until the private one came up.
                        logger.LogWarning(
                            "Refused an outbound request to {Host} ({Address}): {Reason}",
                            host, address, verdict.Reason);

                        throw new OutboundBlockedException(
                            $"'{host}' resolves to {address}, which Airside will not send to. "
                            + verdict.Detail);
                    }
                }

                // Connected to the address that was checked, not to the hostname —
                // otherwise the resolution above and the resolution the socket
                // performs are two different lookups, and only the first was
                // validated.
                var target = addresses[0];
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(target, port), ct).ConfigureAwait(false);

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }
}

/// <summary>
/// Raised when a destination is refused by the outbound guard.
/// </summary>
/// <remarks>
/// Distinct from a transport failure so a channel can be shown as misconfigured
/// rather than as a server that happens to be down — the first needs the URL
/// changed, the second needs waiting.
/// </remarks>
public sealed class OutboundBlockedException : Exception
{
    public OutboundBlockedException()
    {
    }

    public OutboundBlockedException(string message)
        : base(message)
    {
    }

    public OutboundBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
