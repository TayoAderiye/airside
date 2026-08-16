using System.Net;
using Airside.Core.Domains;
using Airside.Runtime.Dns;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Domains;

/// <summary>
/// Tells the caller whether a hostname is already spoken for.
/// </summary>
/// <remarks>
/// Separated from the store so the checks stay in the runtime layer with the rest
/// of pre-flight, rather than pulling the database into it.
/// </remarks>
public interface IHostnameRegistry
{
    /// <summary>The workload currently holding a hostname, ignoring <paramref name="exclude"/>.</summary>
    Task<string?> WhoHoldsAsync(string hostname, Guid? exclude, CancellationToken ct);

    /// <summary>The hostname the dashboard itself is served on, if one is set.</summary>
    Task<string?> GetDashboardHostnameAsync(CancellationToken ct);
}

/// <inheritdoc />
public sealed class DomainPreflight(
    IDnsInspector dns,
    IExternalReachability reachability,
    IPublicSuffixList suffixes,
    IIssuanceLedger ledger,
    IHostnameRegistry registry,
    ProxyRangeIndex proxyRanges,
    ILogger<DomainPreflight> logger) : IDomainPreflight
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<PreflightReport> RunAsync(PreflightRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var checks = new List<PreflightCheck>();
        var hostname = request.Hostname;

        // Syntax and conflicts first. They need no network, and a hostname that
        // cannot be valid should not cost the user a round of DNS queries.
        checks.AddRange(CheckSyntax(hostname, request.Mode));

        if (checks.Exists(c => c.Severity == PreflightSeverity.Blocking))
        {
            return new PreflightReport(hostname, checks);
        }

        checks.AddRange(await CheckConflictsAsync(hostname, request.ExcludeDomainId, ct).ConfigureAwait(false));

        // Everything below is about whether an ACME challenge can succeed, which
        // only matters for the mode that attempts one.
        if (request.Mode != TlsMode.Automatic)
        {
            return new PreflightReport(hostname, checks);
        }

        checks.AddRange(await CheckDnsAsync(hostname, request.SkipExternalProbes, ct).ConfigureAwait(false));
        checks.Add(await CheckCaaAsync(hostname, ct).ConfigureAwait(false));
        checks.AddRange(await CheckPortsAsync(hostname, request.SkipExternalProbes, ct).ConfigureAwait(false));
        checks.AddRange(await CheckRateLimitsAsync(hostname, ct).ConfigureAwait(false));

        return new PreflightReport(hostname, checks);
    }

    private static IEnumerable<PreflightCheck> CheckSyntax(string hostname, TlsMode mode)
    {
        if (!PublicSuffixList.TryNormalise(hostname, out var normalised, out _) || normalised != hostname)
        {
            yield return new PreflightCheck(
                PreflightChecks.HostnameSyntax, PreflightSeverity.Blocking,
                "That is not a valid hostname.",
                Found: hostname,
                Remedy: "Use labels of letters, digits, and hyphens separated by dots, for example "
                    + "app.example.com. Internationalised names are converted to punycode automatically.");

            yield break;
        }

        if (hostname.Length > 253)
        {
            yield return new PreflightCheck(
                PreflightChecks.HostnameSyntax, PreflightSeverity.Blocking,
                "The hostname is too long.",
                Found: $"{hostname.Length} characters", Expected: "253 characters or fewer");

            yield break;
        }

        if (Array.Exists(hostname.Split('.'), l => l.Length is 0 or > 63))
        {
            yield return new PreflightCheck(
                PreflightChecks.HostnameSyntax, PreflightSeverity.Blocking,
                "One of the labels in the hostname is empty or too long.",
                Expected: "Each label between dots must be 1 to 63 characters.");

            yield break;
        }

        if (mode != TlsMode.Automatic)
        {
            yield break;
        }

        if (hostname.StartsWith("*.", StringComparison.Ordinal))
        {
            // HTTP-01 proves control of one name by serving a file at it, which a
            // wildcard has no way to do. Only DNS-01 can validate one.
            yield return new PreflightCheck(
                PreflightChecks.HostnameWildcard, PreflightSeverity.Blocking,
                "A wildcard certificate cannot be issued automatically.",
                Found: hostname,
                Remedy: "Wildcards need a DNS-01 challenge, which Airside does not run yet. Either add "
                    + "each hostname individually, or upload a wildcard certificate using Manual mode.");

            yield break;
        }

        if (IPAddress.TryParse(hostname, out _))
        {
            yield return new PreflightCheck(
                PreflightChecks.HostnameReserved, PreflightSeverity.Blocking,
                "A certificate cannot be issued for an IP address.",
                Found: hostname,
                Remedy: "Use a domain name, or choose Internal mode if this is only reached by address.");

            yield break;
        }

        var reserved = new[] { ".local", ".localhost", ".internal", ".test", ".invalid", ".example" };

        if (hostname is "localhost"
            || Array.Exists(reserved, r => hostname.EndsWith(r, StringComparison.Ordinal)))
        {
            yield return new PreflightCheck(
                PreflightChecks.HostnameReserved, PreflightSeverity.Blocking,
                "That name is reserved and can never pass a public certificate challenge.",
                Found: hostname,
                Remedy: "Use a publicly registered domain, or choose Internal mode to serve a self-signed "
                    + "certificate for this name.");
        }
    }

    private async Task<IEnumerable<PreflightCheck>> CheckConflictsAsync(
        string hostname, Guid? exclude, CancellationToken ct)
    {
        var checks = new List<PreflightCheck>();

        var dashboard = await registry.GetDashboardHostnameAsync(ct).ConfigureAwait(false);

        if (dashboard is not null && string.Equals(dashboard, hostname, StringComparison.Ordinal))
        {
            checks.Add(new PreflightCheck(
                PreflightChecks.HostnameDashboard, PreflightSeverity.Blocking,
                "That hostname is how Airside itself is reached.",
                Found: hostname,
                Remedy: "Routing it to an application would take the dashboard offline. Choose another "
                    + "hostname, or change the dashboard's own domain first."));

            return checks;
        }

        var holder = await registry.WhoHoldsAsync(hostname, exclude, ct).ConfigureAwait(false);

        if (holder is not null)
        {
            // Named rather than reported as "already in use". On a host with a
            // dozen applications, the next question is always which one.
            checks.Add(new PreflightCheck(
                PreflightChecks.HostnameConflict, PreflightSeverity.Blocking,
                $"'{hostname}' is already routed to '{holder}'.",
                Found: holder,
                Remedy: $"Detach it from '{holder}' first. One hostname can serve one application, or "
                    + "which one receives a request would depend on the order the routes happen to be in."));
        }

        return checks;
    }

    private async Task<IEnumerable<PreflightCheck>> CheckDnsAsync(
        string hostname, bool skipExternal, CancellationToken ct)
    {
        var checks = new List<PreflightCheck>();
        var lookup = await dns.LookupAsync(hostname, ct).ConfigureAwait(false);

        if (lookup.Failed)
        {
            // Distinct from NXDOMAIN. Telling someone to create an A record when
            // the real problem is that this host cannot reach a resolver sends
            // them off to fix something that is not broken.
            checks.Add(new PreflightCheck(
                PreflightChecks.DnsUnresolved, PreflightSeverity.Unknown,
                "The DNS lookup could not be completed.",
                Found: lookup.FailureReason,
                Remedy: "This host may not be able to reach a public DNS resolver. Check outbound UDP 53."));

            return checks;
        }

        if (!lookup.HasRecords)
        {
            var expected = skipExternal
                ? null
                : (await reachability.GetPublicAddressAsync(ct).ConfigureAwait(false))?.ToString();

            checks.Add(new PreflightCheck(
                PreflightChecks.DnsUnresolved, PreflightSeverity.Blocking,
                $"'{hostname}' does not resolve to anything.",
                Found: "no A or AAAA records",
                Expected: expected is null ? null : $"an A record pointing to {expected}",
                Remedy: expected is null
                    ? "Create an A record for this hostname pointing at this server's public address."
                    : $"Create an A record for '{hostname}' pointing to {expected}.",

                // DNS changes made moments ago will not have propagated. A hard
                // failure here sends people to change a record they just set
                // correctly.
                RetryAfter: DateTimeOffset.UtcNow.AddMinutes(1)));

            return checks;
        }

        if (lookup.CnameChain.Count > 0 && hostname.Count(c => c == '.') == 1)
        {
            // A CNAME on an apex is invalid per RFC 1034 — but Cloudflare, Route
            // 53, and others flatten it transparently, so a chain that resolves to
            // the right address is working and must not be called an error.
            checks.Add(new PreflightCheck(
                PreflightChecks.DnsApexCname, PreflightSeverity.Warning,
                "The apex of this domain is a CNAME, which is not strictly valid DNS.",
                Found: string.Join(" → ", lookup.CnameChain),
                Remedy: "Your provider appears to be flattening it, so this works. Some registrars reject "
                    + "it outright — if you move providers, use an A record or an ALIAS record instead."));
        }

        var proxied = lookup.V4.Concat(lookup.V6).Where(proxyRanges.IsKnownProxy).ToList();

        if (proxied.Count > 0)
        {
            // Orange-cloud. The address is a CDN edge, so the HTTP-01 challenge
            // is answered there and never reaches this host.
            checks.Add(new PreflightCheck(
                PreflightChecks.DnsProxied, PreflightSeverity.Blocking,
                "This hostname points at Cloudflare's network, not at this server.",
                Found: string.Join(", ", proxied),
                Remedy: "Automatic certificates cannot be issued through a proxied record: the challenge "
                    + "is answered at Cloudflare's edge. Either switch the record to DNS-only (grey "
                    + "cloud) until the certificate is issued, or use External mode and let Cloudflare "
                    + "terminate TLS."));

            return checks;
        }

        if (skipExternal)
        {
            checks.Add(new PreflightCheck(
                PreflightChecks.DnsMatches, PreflightSeverity.Unknown,
                "The hostname resolves, but it was not compared against this server's address.",
                Found: string.Join(", ", lookup.V4.Concat(lookup.V6)),
                Remedy: "External probes are switched off. Set a public address override to enable this "
                    + "check without any outbound calls."));

            return checks;
        }

        var publicAddress = await reachability.GetPublicAddressAsync(ct).ConfigureAwait(false);

        if (publicAddress is null)
        {
            checks.Add(new PreflightCheck(
                PreflightChecks.DnsMatches, PreflightSeverity.Unknown,
                "This server's public address could not be determined, so the record was not verified.",
                Found: string.Join(", ", lookup.V4.Concat(lookup.V6)),
                Remedy: "Set the public address override in settings so this check can run."));

            return checks;
        }

        var all = lookup.V4.Concat(lookup.V6).ToList();
        var matches = all.Exists(ip => ip.Equals(publicAddress));

        if (!matches)
        {
            checks.Add(new PreflightCheck(
                PreflightChecks.DnsPointsElsewhere, PreflightSeverity.Blocking,
                $"'{hostname}' resolves to a different server.",
                Found: string.Join(", ", all),
                Expected: publicAddress.ToString(),
                Remedy: $"Change the A record for '{hostname}' to {publicAddress}. If you changed it "
                    + "recently, propagation can take up to the record's previous TTL.",
                RetryAfter: DateTimeOffset.UtcNow.AddMinutes(2)));
        }
        else
        {
            checks.Add(new PreflightCheck(
                PreflightChecks.DnsMatches, PreflightSeverity.Passed,
                $"'{hostname}' resolves to this server.",
                Found: publicAddress.ToString()));
        }

        if (lookup.V4.Count > 1)
        {
            // Legitimate behind a load balancer, so a warning rather than a block
            // — but the challenge may land on a different host, which is worth
            // saying before it does.
            checks.Add(new PreflightCheck(
                PreflightChecks.DnsMultipleRecords, PreflightSeverity.Warning,
                "This hostname has several A records.",
                Found: string.Join(", ", lookup.V4),
                Remedy: "Requests are shared between them, so a certificate challenge may be answered by "
                    + "another server and fail. This is fine behind a load balancer that forwards to this "
                    + "host; otherwise remove the other records."));
        }

        checks.AddRange(await CheckIpv6Async(lookup, ct).ConfigureAwait(false));

        return checks;
    }

    /// <summary>
    /// The AAAA trap.
    /// </summary>
    /// <remarks>
    /// Let's Encrypt prefers IPv6 whenever an AAAA record exists. A host with a
    /// perfect A record and a stale or non-routable AAAA record fails validation
    /// every time, and the error says nothing about IPv6 — this is hours of
    /// someone's life, and it is entirely detectable in advance.
    /// </remarks>
    private async Task<IEnumerable<PreflightCheck>> CheckIpv6Async(DnsLookup lookup, CancellationToken ct)
    {
        if (lookup.V6.Count == 0)
        {
            return [];
        }

        foreach (var address in lookup.V6)
        {
            if (await ExternalReachability.CanOpenAsync(address, 80, ProbeTimeout).ConfigureAwait(false))
            {
                return [];
            }

            ct.ThrowIfCancellationRequested();
        }

        logger.LogInformation(
            "{Hostname} has AAAA records that this host cannot reach on port 80", lookup.Hostname);

        return
        [
            new PreflightCheck(
                PreflightChecks.DnsIpv6Unreachable, PreflightSeverity.Blocking,
                "This hostname has an IPv6 record that does not answer.",
                Found: string.Join(", ", lookup.V6),
                Remedy: "Certificate authorities prefer IPv6 when an AAAA record exists, so validation "
                    + "will fail even though the IPv4 record is correct. Remove the AAAA record, or fix "
                    + "IPv6 connectivity to this server."),
        ];
    }

    private async Task<PreflightCheck> CheckCaaAsync(string hostname, CancellationToken ct)
    {
        var records = await dns.LookupCaaAsync(hostname, ct).ConfigureAwait(false);

        var issue = records.Where(r =>
            string.Equals(r.Tag, "issue", StringComparison.OrdinalIgnoreCase)).ToList();

        if (issue.Count == 0)
        {
            // No CAA at all means every authority is permitted, which is the
            // default and is fine.
            return new PreflightCheck(
                PreflightChecks.Caa, PreflightSeverity.Passed,
                "No CAA record restricts who may issue for this domain.");
        }

        var permitted = issue.Exists(r =>
            r.Value.Contains("letsencrypt.org", StringComparison.OrdinalIgnoreCase)
            || r.Value.Trim() == ";");

        return permitted
            ? new PreflightCheck(
                PreflightChecks.Caa, PreflightSeverity.Passed,
                "The CAA record permits Let's Encrypt.",
                Found: string.Join(", ", issue.Select(r => r.Value)))
            : new PreflightCheck(
                PreflightChecks.Caa, PreflightSeverity.Blocking,
                "A CAA record on this domain forbids Let's Encrypt from issuing.",
                Found: string.Join(", ", issue.Select(r => $"issue \"{r.Value}\"")),
                Expected: "issue \"letsencrypt.org\"",
                Remedy: "Add a CAA record authorising letsencrypt.org, or remove the existing CAA record. "
                    + "Issuance fails at the authority regardless of DNS and firewall settings.");
    }

    private async Task<IEnumerable<PreflightCheck>> CheckPortsAsync(
        string hostname, bool skipExternal, CancellationToken ct)
    {
        var checks = new List<PreflightCheck>();

        var holder = await reachability.WhoHoldsAsync(80, ct).ConfigureAwait(false);

        if (holder is not null && !holder.IsAirsideProxy)
        {
            checks.Add(new PreflightCheck(
                PreflightChecks.PortConflict, PreflightSeverity.Blocking,
                "Something else on this host is using port 80.",
                Found: holder.Process,
                Remedy: $"'{holder.Process}' has claimed port 80, so Airside's proxy cannot answer the "
                    + "certificate challenge. Stop it, or move it to another port."));

            return checks;
        }

        if (holder is null)
        {
            // Docker shows nothing publishing 80. That may be a host process
            // outside Docker, which cannot be named from inside this container.
            checks.Add(new PreflightCheck(
                PreflightChecks.PortConflict, PreflightSeverity.Warning,
                "Airside's proxy does not appear to be publishing port 80.",
                Remedy: "Check that the airside-proxy container is running and that no other service on "
                    + "the host — a system nginx or Apache is common on a repurposed server — has taken "
                    + "the port."));
        }

        if (skipExternal)
        {
            return checks;
        }

        foreach (var port in new[] { 80, 443 })
        {
            var probe = await reachability.ProbeAsync(hostname, port, ct).ConfigureAwait(false);
            var id = port == 80 ? PreflightChecks.PortHttp : PreflightChecks.PortHttps;

            checks.Add(probe.Reachable switch
            {
                true => new PreflightCheck(id, PreflightSeverity.Passed, $"Port {port} is reachable."),

                false => new PreflightCheck(
                    id,
                    port == 80 ? PreflightSeverity.Blocking : PreflightSeverity.Warning,
                    $"Port {port} is not reachable from the internet.",
                    Remedy: port == 80
                        ? "The certificate challenge is delivered over port 80, so it must be open even "
                          + "if your site only serves HTTPS. A cloud firewall or security group is the "
                          + "usual cause and is invisible from inside this host — check the provider "
                          + "console as well as ufw or iptables."
                        : "Traffic to your site will not arrive until port 443 is open."),

                null => new PreflightCheck(
                    id, PreflightSeverity.Unknown,
                    $"Whether port {port} is reachable could not be determined.",
                    Found: probe.Detail),
            });
        }

        return checks;
    }

    private async Task<IEnumerable<PreflightCheck>> CheckRateLimitsAsync(string hostname, CancellationToken ct)
    {
        var registered = suffixes.GetRegisteredDomain(hostname);

        if (registered is null)
        {
            return [];
        }

        var assessment = await ledger.AssessAsync(hostname, staging: false, ct).ConfigureAwait(false);

        return assessment.Findings;
    }
}
