using Airside.Runtime.Dns;

namespace Airside.Tests.Domains;

/// <summary>
/// Registered-domain extraction, which ACME rate-limit accounting is keyed on.
/// </summary>
/// <remarks>
/// Getting this wrong is quiet and misleading in both directions. Treating
/// <c>co.uk</c> as the registered domain would pool every unrelated UK name into
/// one bucket and warn about a limit nobody is near; missing a private-section
/// suffix would count several independent sites as one.
/// </remarks>
public class PublicSuffixListTests
{
    private readonly PublicSuffixList _list = new();

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("app.example.com", "example.com")]
    [InlineData("a.b.c.example.com", "example.com")]
    public void SimpleSuffixes(string hostname, string expected) =>
        Assert.Equal(expected, _list.GetRegisteredDomain(hostname));

    [Theory]
    [InlineData("example.co.uk", "example.co.uk")]
    [InlineData("shop.example.co.uk", "example.co.uk")]
    [InlineData("example.com.au", "example.com.au")]
    public void MultiLabelSuffixes(string hostname, string expected) =>
        Assert.Equal(expected, _list.GetRegisteredDomain(hostname));

    [Theory]
    [InlineData("mysite.github.io", "mysite.github.io")]
    [InlineData("app.mysite.github.io", "mysite.github.io")]
    public void PrivateSectionSuffixesCountSeparately(string hostname, string expected)
    {
        // Kept because Let's Encrypt counts against the full list. Two sites on
        // github.io are two registered domains, not one shared allowance.
        Assert.Equal(expected, _list.GetRegisteredDomain(hostname));
    }

    [Fact]
    public void WildcardRulesConsumeExactlyOneLabel()
    {
        // *.ck is a wildcard rule, so test.ck is a suffix and example.test.ck is
        // the registrable name below it.
        Assert.Equal("example.test.ck", _list.GetRegisteredDomain("example.test.ck"));
    }

    [Fact]
    public void ExceptionRulesBeatWildcards()
    {
        // !city.kawasaki.jp overrides *.kawasaki.jp, making city.kawasaki.jp
        // itself registrable.
        Assert.Equal("city.kawasaki.jp", _list.GetRegisteredDomain("city.kawasaki.jp"));
    }

    [Theory]
    [InlineData("co.uk")]
    [InlineData("com")]
    [InlineData("github.io")]
    public void APublicSuffixHasNoRegisteredDomain(string hostname) =>
        Assert.Null(_list.GetRegisteredDomain(hostname));

    [Fact]
    public void AnUnknownTldIsTreatedAsASingleLabelSuffix() =>
        Assert.Equal("example.invalid", _list.GetRegisteredDomain("app.example.invalid"));

    [Theory]
    [InlineData("APP.Example.COM", "app.example.com", "app.example.com")]
    [InlineData("app.example.com.", "app.example.com", "app.example.com")]
    [InlineData("bücher.example.com", "xn--bcher-kva.example.com", "bücher.example.com")]
    public void NormalisationProducesPunycodeAndKeepsTheDisplayForm(
        string input, string expectedPunycode, string expectedDisplay)
    {
        // Comparison and storage use punycode because that is what DNS, the SAN
        // list, and Caddy's matcher all carry. The display form is kept apart so
        // a lookalike name cannot shadow a real one through the comparison path.
        Assert.True(PublicSuffixList.TryNormalise(input, out var punycode, out var display));
        Assert.Equal(expectedPunycode, punycode);
        Assert.Equal(expectedDisplay, display);
    }

    [Fact]
    public void NormalisationRejectsRatherThanRepairs()
    {
        Assert.False(PublicSuffixList.TryNormalise("exa mple.com", out _, out _));
        Assert.False(PublicSuffixList.TryNormalise("  ", out _, out _));
    }
}
