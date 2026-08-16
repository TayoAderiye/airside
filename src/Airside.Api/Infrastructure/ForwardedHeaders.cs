using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Airside.Api.Infrastructure;

public sealed class ForwardedHeaderOptions
{
    public const string Section = "Airside:ForwardedHeaders";

    /// <summary>
    /// Proxies whose forwarded headers are believed. Empty by default.
    /// </summary>
    /// <remarks>
    /// Nothing is trusted until an operator names it. See
    /// <see cref="ForwardedHeaderSetup"/> for why the empty default is the safe
    /// one and why the obvious alternative is dangerous.
    /// </remarks>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>CIDR ranges, for a proxy that does not have one stable address.</summary>
    public IList<string> KnownNetworks { get; } = [];
}

/// <summary>
/// Configures which forwarded headers are believed, and from whom.
/// </summary>
/// <remarks>
/// <para>
/// Airside's own proxy runs on a known container network, so its headers are
/// trusted. Anything in front of that — an ALB, CloudFront, Cloudflare, a
/// corporate proxy — has to be named by the operator.
/// </para>
/// <para>
/// <b>Never trust forwarded headers unconditionally.</b> The tempting shortcut is
/// to clear the known-proxy list, which ASP.NET Core reads as "believe everyone",
/// and it appears to work perfectly. What it actually does is let anyone who can
/// reach the origin directly write whatever they like into
/// <c>X-Forwarded-For</c>: audit entries then record an attacker's chosen address,
/// rate limiting keys on a value the caller controls, and the audit log is worse
/// than useless because it looks authoritative while being fiction.
/// </para>
/// <para>
/// So an unconfigured instance trusts only its own proxy, and a misconfigured one
/// records the proxy's address rather than a forged one. Both are wrong in the
/// direction that cannot be exploited.
/// </para>
/// </remarks>
public static class ForwardedHeaderSetup
{
    public static IServiceCollection AddAirsideForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ForwardedHeaderOptions();
        configuration.GetSection(ForwardedHeaderOptions.Section).Bind(options);

        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            // The defaults contain loopback, which is not this deployment's shape:
            // the API sees the proxy's container address, not 127.0.0.1. Cleared
            // so the trusted set is exactly what is added below and nothing else.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();

            // Docker's default bridge pool. The proxy is the only thing on the
            // internal network that talks to the API, so this is the narrowest
            // range that still works without pinning a container IP that changes
            // whenever the proxy is replaced.
            forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));

            foreach (var proxy in options.KnownProxies)
            {
                if (IPAddress.TryParse(proxy.Trim(), out var address))
                {
                    forwarded.KnownProxies.Add(address);
                }
            }

            foreach (var network in options.KnownNetworks)
            {
                var parts = network.Split('/');

                if (parts.Length == 2
                    && IPAddress.TryParse(parts[0], out var prefix)
                    && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var length))
                {
                    forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, length));
                }
            }

            // One hop by default: the Airside proxy. An operator running behind a
            // CDN sets this higher along with the ranges above — and has to,
            // because each additional hop is another party permitted to rewrite
            // the client address.
            forwarded.ForwardLimit = 1 + options.KnownProxies.Count + options.KnownNetworks.Count;
        });

        return services;
    }
}
