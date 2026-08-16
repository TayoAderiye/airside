using System.Globalization;
using System.Reflection;
using Airside.Core.Domains;

namespace Airside.Runtime.Dns;

/// <summary>
/// The Public Suffix List, read once from an embedded resource.
/// </summary>
/// <remarks>
/// <para>
/// Implements the matching algorithm from publicsuffix.org: take the longest
/// matching rule, where an exception rule (<c>!</c>) wins over any wildcard, and a
/// wildcard (<c>*</c>) matches exactly one label. The registered domain is then
/// one label more than the matched suffix.
/// </para>
/// <para>
/// The private section is kept as well as the ICANN section, because Let's
/// Encrypt counts against the full list — names under <c>github.io</c> or
/// <c>herokuapp.com</c> each count as their own registered domain, which is the
/// behaviour a user relying on those will expect.
/// </para>
/// </remarks>
public sealed class PublicSuffixList : IPublicSuffixList
{
    private const string ResourceName = "Airside.Runtime.Dns.data.public_suffix_list.dat";

    private readonly HashSet<string> _rules;
    private readonly HashSet<string> _wildcards;
    private readonly HashSet<string> _exceptions;

    public PublicSuffixList()
    {
        _rules = new HashSet<string>(StringComparer.Ordinal);
        _wildcards = new HashSet<string>(StringComparer.Ordinal);
        _exceptions = new HashSet<string>(StringComparer.Ordinal);

        using var stream = typeof(PublicSuffixList).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The Public Suffix List resource '{ResourceName}' is missing from the assembly.");

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            var rule = line.Trim();

            if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.StartsWith('!'))
            {
                _exceptions.Add(rule[1..]);
            }
            else if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                _wildcards.Add(rule[2..]);
            }
            else
            {
                _rules.Add(rule);
            }
        }

        Version = "embedded";
    }

    public string Version { get; }

    public string? GetRegisteredDomain(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var name = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        var labels = name.Split('.');

        if (labels.Length < 2)
        {
            return null;
        }

        // Walk from the most specific candidate suffix inward. The first
        // exception match ends the search immediately: an exception rule such as
        // !city.kawasaki.jp says the name above it is registrable after all.
        for (var i = 0; i < labels.Length; i++)
        {
            var candidate = string.Join('.', labels[i..]);

            if (_exceptions.Contains(candidate))
            {
                return candidate;
            }
        }

        var suffixLength = 0;

        for (var i = labels.Length - 1; i >= 0; i--)
        {
            var candidate = string.Join('.', labels[i..]);
            var parent = i + 1 < labels.Length ? string.Join('.', labels[(i + 1)..]) : null;

            if (_rules.Contains(candidate) || (parent is not null && _wildcards.Contains(parent)))
            {
                suffixLength = labels.Length - i;
            }
        }

        // No rule matched at all. The list's own default is that an unknown TLD
        // behaves as a single-label suffix, so example.invalid is registrable.
        if (suffixLength == 0)
        {
            suffixLength = 1;
        }

        // The hostname is itself a public suffix, so there is nothing registrable
        // below it — "co.uk" has no owner to count certificates against.
        return suffixLength >= labels.Length
            ? null
            : string.Join('.', labels[(labels.Length - suffixLength - 1)..]);
    }

    /// <summary>
    /// Normalises a hostname to lowercase punycode.
    /// </summary>
    /// <remarks>
    /// Storage and comparison are always on the punycode form, because that is
    /// what DNS carries, what a certificate's SAN list contains, and what Caddy
    /// matches on. Keeping the Unicode form separately for display is deliberate:
    /// two visually identical names can differ in codepoints, and comparing the
    /// display form would let one domain shadow another.
    /// </remarks>
    public static bool TryNormalise(string? hostname, out string punycode, out string display)
    {
        punycode = string.Empty;
        display = string.Empty;

        if (string.IsNullOrWhiteSpace(hostname))
        {
            return false;
        }

        display = hostname.Trim().TrimEnd('.').ToLowerInvariant();

        // A leading "*." is set aside before IDN mapping and put back after.
        // IdnMapping rejects the asterisk outright, which would make a wildcard
        // fail as "not a valid hostname" — burying the message that actually helps
        // (that wildcards need DNS-01, or an uploaded certificate) behind a syntax
        // error that says nothing about wildcards at all.
        var wildcard = display.StartsWith("*.", StringComparison.Ordinal);
        var bare = wildcard ? display[2..] : display;

        if (bare.Length == 0)
        {
            return false;
        }

        try
        {
            var ascii = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true }
                .GetAscii(bare);

            punycode = wildcard ? "*." + ascii : ascii;

            return true;
        }
        catch (ArgumentException)
        {
            // Invalid IDN input. Reported as a syntax failure by the caller rather
            // than sanitised into something that might resolve elsewhere.
            return false;
        }
    }
}
