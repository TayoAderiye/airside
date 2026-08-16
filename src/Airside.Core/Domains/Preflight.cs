namespace Airside.Core.Domains;

/// <summary>
/// Everything checked before Airside lets Caddy attempt an ACME challenge.
/// </summary>
/// <remarks>
/// <para>
/// This exists because Caddy's ACME retry loop is opaque. A hostname that does
/// not resolve here produces challenge failures on a backoff, with no message a
/// user can act on, for as long as they leave it alone. The information needed to
/// explain it — what the name resolves to, what it should resolve to, whether
/// port 80 is open — is all available <em>before</em> the attempt, so it is
/// gathered there and reported at the moment of the mistake.
/// </para>
/// <para>
/// Every check names the value it found and the value it expected. "Validation
/// failed" is not an acceptable message here; "app.example.com resolves to
/// 203.0.113.9, but this server is 198.51.100.4" is.
/// </para>
/// </remarks>
public interface IDomainPreflight
{
    /// <summary>Runs every check for a hostname. Never throws for a failed check — that is a result.</summary>
    Task<PreflightReport> RunAsync(PreflightRequest request, CancellationToken ct);
}

/// <param name="SkipExternalProbes">
/// Suppresses anything that leaves the host. Reachability of port 80 genuinely
/// cannot be established from inside the machine, so the checks that need it call
/// an outside service — which a self-hosted tool should never do without the
/// operator knowing. With this set the affected checks report as unknown rather
/// than silently passing.
/// </param>
public sealed record PreflightRequest(
    string Hostname,
    TlsMode Mode,
    Guid? ExcludeDomainId = null,
    bool SkipExternalProbes = false);

/// <summary>
/// The outcome of a pre-flight run.
/// </summary>
/// <remarks>
/// A report is advisory except where <see cref="Blocks"/> is true. Several checks
/// describe situations that are legitimate — round-robin DNS behind a load
/// balancer, a CNAME the registrar flattens — and blocking on them would stop
/// people doing correct things.
/// </remarks>
public sealed record PreflightReport(string Hostname, IReadOnlyList<PreflightCheck> Checks)
{
    public bool Blocks => Checks.Any(c => c.Severity == PreflightSeverity.Blocking);

    public bool HasWarnings => Checks.Any(c => c.Severity == PreflightSeverity.Warning);

    public IEnumerable<PreflightCheck> Blocking =>
        Checks.Where(c => c.Severity == PreflightSeverity.Blocking);
}

/// <param name="Id">
/// A stable identifier such as <c>dns.points_elsewhere</c>. The UI keys help
/// links off this, so it is part of the contract and does not change with wording.
/// </param>
/// <param name="Found">What is actually true right now, in the user's terms.</param>
/// <param name="Expected">What it needs to be. Null when the check has nothing to compare against.</param>
/// <param name="Remedy">The action to take. Written as an instruction, not a description.</param>
/// <param name="RetryAfter">
/// Set when the check may succeed later without the user doing anything —
/// DNS propagation, or an ACME rate limit with a known reset.
/// </param>
public sealed record PreflightCheck(
    string Id,
    PreflightSeverity Severity,
    string Summary,
    string? Found = null,
    string? Expected = null,
    string? Remedy = null,
    DateTimeOffset? RetryAfter = null);

public enum PreflightSeverity
{
    /// <summary>Checked and fine.</summary>
    Passed,

    /// <summary>Could not be determined. Reported honestly rather than assumed good.</summary>
    Unknown,

    /// <summary>Legitimate but worth knowing, or a likely mistake that is sometimes deliberate.</summary>
    Warning,

    /// <summary>Issuance cannot succeed. The attempt is not made.</summary>
    Blocking,
}

/// <summary>Check identifiers. Referenced by the UI, so they are constants rather than literals.</summary>
public static class PreflightChecks
{
    public const string HostnameSyntax = "hostname.syntax";
    public const string HostnameReserved = "hostname.reserved";
    public const string HostnameWildcard = "hostname.wildcard";
    public const string HostnameConflict = "hostname.conflict";
    public const string HostnameDashboard = "hostname.dashboard";

    public const string DnsUnresolved = "dns.unresolved";
    public const string DnsPointsElsewhere = "dns.points_elsewhere";
    public const string DnsMatches = "dns.matches";
    public const string DnsApexCname = "dns.apex_cname";
    public const string DnsMultipleRecords = "dns.multiple_records";
    public const string DnsIpv6Unreachable = "dns.ipv6_unreachable";
    public const string DnsProxied = "dns.proxied";

    public const string PortHttp = "port.http";
    public const string PortHttps = "port.https";
    public const string PortConflict = "port.conflict";

    public const string Caa = "caa";
    public const string RateLimit = "rate_limit";
}
