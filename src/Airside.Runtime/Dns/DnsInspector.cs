using System.Net;
using Airside.Core.Domains;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CaaRecord = Airside.Core.Domains.CaaRecord;
using DnsCaaRecord = DnsClient.Protocol.CaaRecord;

namespace Airside.Runtime.Dns;

public sealed class DnsOptions
{
    public const string Section = "Airside:Dns";

    /// <summary>
    /// Public resolvers, queried instead of the host's own.
    /// </summary>
    /// <remarks>
    /// The point of pre-flight is to see the hostname the way Let's Encrypt will.
    /// A host with split-horizon DNS, an internal resolver, or a leftover
    /// <c>/etc/hosts</c> entry reports a name resolving perfectly while the
    /// public internet sees something else entirely — and that is precisely the
    /// situation the check exists to catch.
    /// </remarks>
    public IList<string> Resolvers { get; } = ["1.1.1.1", "8.8.8.8"];

    public int TimeoutSeconds { get; set; } = 5;
}

/// <inheritdoc />
public sealed class DnsInspector : IDnsInspector
{
    private readonly LookupClient _client;
    private readonly ILogger<DnsInspector> _logger;

    public DnsInspector(IOptions<DnsOptions> options, ILogger<DnsInspector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;

        var endpoints = options.Value.Resolvers
            .Select(r => IPAddress.TryParse(r, out var ip) ? new IPEndPoint(ip, 53) : null)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToArray();

        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException(
                "No usable DNS resolvers are configured. Pre-flight must query a public resolver rather "
                + "than the host's, or it cannot see what Let's Encrypt will see.");
        }

        _client = new LookupClient(new LookupClientOptions(endpoints)
        {
            Timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds),

            // The whole purpose is a fresh, authoritative view. A cached answer
            // would hide the DNS change the user just made and report the old
            // value back at them.
            UseCache = false,
            Retries = 1,
            ThrowDnsErrors = false,
        });
    }

    public async Task<DnsLookup> LookupAsync(string hostname, CancellationToken ct)
    {
        try
        {
            var a = await _client.QueryAsync(hostname, QueryType.A, cancellationToken: ct).ConfigureAwait(false);
            var aaaa = await _client.QueryAsync(hostname, QueryType.AAAA, cancellationToken: ct).ConfigureAwait(false);

            var v4 = a.Answers.ARecords().Select(r => r.Address).ToList();
            var v6 = aaaa.Answers.AaaaRecords().Select(r => r.Address).ToList();

            // Collected from both queries: a name may be a CNAME whose target
            // carries only one address family.
            var chain = a.Answers.CnameRecords()
                .Concat(aaaa.Answers.CnameRecords())
                .Select(c => c.CanonicalName.Value.TrimEnd('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DnsLookup(hostname, v4, v6, chain);
        }
        catch (DnsResponseException ex)
        {
            // A resolver that cannot be reached is not the same as a name that
            // does not exist, and telling a user to create an A record when the
            // real problem is egress DNS being blocked sends them the wrong way.
            _logger.LogWarning(ex, "DNS lookup for {Hostname} failed", hostname);

            return new DnsLookup(hostname, [], [], [], Failed: true, ex.DnsError.ToString());
        }
    }

    public async Task<IReadOnlyList<CaaRecord>> LookupCaaAsync(string hostname, CancellationToken ct)
    {
        // CAA is inherited down the tree, so a record on example.com governs
        // app.example.com even though the child has none of its own. The first
        // level that answers wins; the search stops there.
        var labels = hostname.Split('.');

        for (var i = 0; i < labels.Length - 1; i++)
        {
            var name = string.Join('.', labels[i..]);

            try
            {
                var response = await _client.QueryAsync(name, QueryType.CAA, cancellationToken: ct)
                    .ConfigureAwait(false);

                var records = response.Answers
                    .OfType<DnsCaaRecord>()
                    .Select(r => new CaaRecord(r.Flags, r.Tag, r.Value))
                    .ToList();

                if (records.Count > 0)
                {
                    return records;
                }
            }
            catch (DnsResponseException ex)
            {
                _logger.LogWarning(ex, "CAA lookup for {Name} failed", name);
                return [];
            }
        }

        return [];
    }
}
