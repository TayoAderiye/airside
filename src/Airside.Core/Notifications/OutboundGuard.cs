using System.Net;
using System.Net.Sockets;

namespace Airside.Core.Notifications;

/// <summary>
/// Decides whether Airside may make an outbound request to an address.
/// </summary>
/// <remarks>
/// <para>
/// A webhook is a way for a user to make <em>the server</em> issue an HTTP
/// request, and this server is a bad one to have that power over. It holds the
/// Docker socket, and it shares a network with Caddy's admin API — which is
/// unauthenticated and can load configuration that executes commands. A webhook
/// pointed at <c>http://airside-proxy:2019/load</c> would hand over every route on
/// the host to anyone who can configure a notification channel.
/// </para>
/// <para>
/// The cloud metadata services are the other target: a POST to
/// <c>169.254.169.254</c> returns IAM credentials on AWS and an access token on
/// GCP and Azure. Both are one link-local address away from any process allowed to
/// choose a URL.
/// </para>
/// <para>
/// So outbound requests are checked against resolved addresses rather than
/// against the hostname. A name is not a destination — <c>evil.example.com</c> can
/// resolve to <c>127.0.0.1</c>, and validating the string would pass it.
/// </para>
/// </remarks>
public static class OutboundGuard
{
    /// <summary>
    /// Ranges Airside will not send a webhook to.
    /// </summary>
    /// <param name="address">The resolved destination, not a hostname.</param>
    /// <param name="allowPrivateNetworks">
    /// Permits RFC 1918 and IPv6 unique-local destinations, for an operator who
    /// genuinely runs a receiver on their own network.
    /// </param>
    /// <remarks>
    /// <para>
    /// Deny-listed rather than allow-listed because the legitimate destination is
    /// "somewhere on the internet", which cannot be enumerated. Every entry is a
    /// range that reaches this host, its neighbours on the container network, or a
    /// metadata service.
    /// </para>
    /// <para>
    /// Loopback and link-local stay refused <b>even when
    /// <paramref name="allowPrivateNetworks"/> is set</b>, and that separation is
    /// the point. An earlier version had one switch for all of it, so turning on
    /// "I have an internal receiver" also re-opened <c>169.254.169.254</c> — the
    /// address that returns cloud credentials — and Airside's own API on loopback.
    /// Neither is ever a legitimate webhook target, so neither is behind the
    /// switch.
    /// </para>
    /// </remarks>
    public static OutboundVerdict Check(IPAddress address, bool allowPrivateNetworks = false)
    {
        ArgumentNullException.ThrowIfNull(address);

        // An IPv4 address wrapped as IPv6 (::ffff:127.0.0.1) is the same
        // destination and must be judged as one — checking the v6 form against v6
        // rules only would let loopback straight through.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return Deny(
                "loopback",
                "That address is this server itself. A webhook to loopback can reach services that are "
                + "deliberately not exposed, including Airside's own API.");
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();

            // 169.254.0.0/16 — link-local, and the home of the cloud metadata
            // services. A request here returns IAM credentials on AWS and an
            // access token on GCP and Azure.
            if (octets[0] == 169 && octets[1] == 254)
            {
                return Deny(
                    "link_local",
                    "That address is link-local. On a cloud instance it is the metadata service, which "
                    + "hands out credentials to anything that can reach it.");
            }

            if (!allowPrivateNetworks
                && (octets[0] == 10
                    || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
                    || (octets[0] == 192 && octets[1] == 168)))
            {
                return Deny(
                    "private_network",
                    "That address is on a private network. From inside this server that includes the "
                    + "container networks, where Airside's proxy exposes an unauthenticated admin API.");
            }

            // 0.0.0.0/8 and 100.64.0.0/10 (carrier-grade NAT, and Tailscale's
            // range) are neither routable nor safe to assume are external.
            if (octets[0] == 0 || (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127))
            {
                return Deny("reserved", "That address is in a reserved range and is not publicly routable.");
            }

            return OutboundVerdict.Allowed;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal)
            {
                return Deny("link_local", "That address is IPv6 link-local.");
            }

            var bytes = address.GetAddressBytes();

            // fc00::/7 — unique local addresses, IPv6's private range.
            if (!allowPrivateNetworks && (bytes[0] & 0xFE) == 0xFC)
            {
                return Deny("private_network", "That address is an IPv6 unique-local address.");
            }

            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            {
                return Deny("reserved", "That address is reserved.");
            }

            return OutboundVerdict.Allowed;
        }

        return Deny("unsupported", "Only IPv4 and IPv6 destinations are supported.");
    }

    private static OutboundVerdict Deny(string reason, string detail) => new(false, reason, detail);
}

/// <param name="Reason">A stable code, so the UI can explain the refusal without parsing prose.</param>
public sealed record OutboundVerdict(bool IsAllowed, string? Reason = null, string? Detail = null)
{
    public static OutboundVerdict Allowed { get; } = new(true);
}
