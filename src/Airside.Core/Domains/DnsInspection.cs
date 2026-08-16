using System.Net;

namespace Airside.Core.Domains;

/// <summary>
/// DNS lookups against a chosen resolver.
/// </summary>
/// <remarks>
/// <para>
/// Not <c>Dns.GetHostAddresses</c>. That uses the host's own resolver, and the
/// host is exactly the machine most likely to be wrong: split-horizon setups, a
/// stale <c>/etc/hosts</c> entry, or an internal resolver will all report a
/// hostname resolving correctly when the public internet — and therefore Let's
/// Encrypt — sees something else. Every check here queries a public resolver on
/// purpose, so the answer is the one the ACME server will get.
/// </para>
/// <para>
/// It also cannot read CAA records at all, and a CAA record that omits Let's
/// Encrypt makes issuance impossible no matter what else is correct.
/// </para>
/// </remarks>
public interface IDnsInspector
{
    Task<DnsLookup> LookupAsync(string hostname, CancellationToken ct);

    Task<IReadOnlyList<CaaRecord>> LookupCaaAsync(string hostname, CancellationToken ct);
}

/// <param name="CnameChain">
/// Present when the name is an alias. A CNAME on an apex is invalid per the DNS
/// specification, but several large providers flatten it transparently — so it is
/// reported rather than rejected when the chain resolves to the right address.
/// </param>
/// <param name="Failed">
/// True when the resolver could not be reached at all, as opposed to answering
/// that the name does not exist. The two mean very different things to a user and
/// must not collapse into one message.
/// </param>
public sealed record DnsLookup(
    string Hostname,
    IReadOnlyList<IPAddress> V4,
    IReadOnlyList<IPAddress> V6,
    IReadOnlyList<string> CnameChain,
    bool Failed = false,
    string? FailureReason = null)
{
    public bool HasRecords => V4.Count > 0 || V6.Count > 0;

    public static DnsLookup Empty(string hostname) => new(hostname, [], [], []);
}

/// <param name="Tag">
/// <c>issue</c>, <c>issuewild</c>, or <c>iodef</c>. Only the first two gate
/// issuance.
/// </param>
public sealed record CaaRecord(byte Flags, string Tag, string Value);

/// <summary>
/// The host's own public address, and whether ports are reachable from outside.
/// </summary>
/// <remarks>
/// Both questions are unanswerable from inside the machine. A socket bound to
/// port 80 says nothing about whether a cloud security group, a provider
/// firewall, or <c>ufw</c> lets a packet reach it, and those are invisible from
/// the host. So this reaches an outside service — which is a real consideration
/// for a self-hosted tool, and is why the endpoints are configurable and the
/// whole thing can be switched off.
/// </remarks>
public interface IExternalReachability
{
    /// <summary>The address the internet sees, or null when it cannot be established.</summary>
    Task<IPAddress?> GetPublicAddressAsync(CancellationToken ct);

    Task<PortProbe> ProbeAsync(string hostname, int port, CancellationToken ct);

    /// <summary>
    /// Whether something on this host is already listening on the port.
    /// </summary>
    /// <remarks>
    /// A repurposed box very often has nginx or Apache still bound to 80, which
    /// stops Caddy binding it and produces an ACME failure that looks like a DNS
    /// problem. Naming the process turns a long investigation into one line.
    /// </remarks>
    Task<LocalPortHolder?> WhoHoldsAsync(int port, CancellationToken ct);
}

/// <param name="Reachable">Null when the probe itself could not be carried out.</param>
public sealed record PortProbe(int Port, bool? Reachable, string? Detail = null);

public sealed record LocalPortHolder(int Port, string Process, bool IsAirsideProxy);

/// <summary>
/// Splits a hostname into its registrable part.
/// </summary>
/// <remarks>
/// Needed because Let's Encrypt counts certificates per registered domain, and
/// naive splitting gets that wrong in ways that matter: the registered domain of
/// <c>shop.example.co.uk</c> is <c>example.co.uk</c>, not <c>co.uk</c>. Counting
/// against <c>co.uk</c> would pool unrelated names together and produce warnings
/// about a limit nobody is near.
/// </remarks>
public interface IPublicSuffixList
{
    /// <summary>Returns eTLD+1, or null when the hostname is itself a public suffix.</summary>
    string? GetRegisteredDomain(string hostname);
}
