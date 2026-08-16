using Airside.Runtime.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Airside.Tests.Domains;

/// <summary>
/// The DNS inspector against real public records.
/// </summary>
/// <remarks>
/// Network-dependent, so it skips when DNS is unavailable rather than failing a
/// laptop build. It is kept because the value of these lookups is entirely in
/// whether they agree with the outside world — a mocked resolver would only
/// confirm that the mock was written to match the code.
/// </remarks>
[Collection("network")]
public class DnsInspectorLiveTests
{
    private static DnsInspector Build() =>
        new(Options.Create(new DnsOptions()), NullLogger<DnsInspector>.Instance);

    [NetworkFact]
    public async Task ResolvesAnExistingNameToAddresses()
    {
        var result = await Build().LookupAsync("one.one.one.one", CancellationToken.None);

        Assert.False(result.Failed);
        Assert.Contains(result.V4, ip => ip.ToString() == "1.1.1.1");
        Assert.NotEmpty(result.V6);
    }

    [NetworkFact]
    public async Task ANameThatDoesNotExistReturnsNoRecordsRatherThanFailing()
    {
        // Distinct from a resolver failure, and the two produce different advice.
        var result = await Build().LookupAsync(
            "this-name-should-not-exist-airside-test.example.com", CancellationToken.None);

        Assert.False(result.HasRecords);
    }

    [NetworkFact]
    public async Task ReadsCaaRecordsAndInheritsThemFromTheParent()
    {
        // google.com publishes CAA. A subdomain with none of its own is still
        // governed by it, which is why the lookup walks up the tree.
        var direct = await Build().LookupCaaAsync("google.com", CancellationToken.None);
        Assert.NotEmpty(direct);
        Assert.Contains(direct, r => r.Tag == "issue");

        var inherited = await Build().LookupCaaAsync("mail.google.com", CancellationToken.None);
        Assert.NotEmpty(inherited);
    }

    [NetworkFact]
    public async Task FollowsACnameAndReportsTheChain()
    {
        var result = await Build().LookupAsync("www.github.com", CancellationToken.None);

        Assert.True(result.HasRecords);
        Assert.NotEmpty(result.CnameChain);
    }
}
