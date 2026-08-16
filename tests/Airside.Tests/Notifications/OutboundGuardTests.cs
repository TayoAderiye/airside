using System.Net;
using Airside.Core.Notifications;

namespace Airside.Tests.Notifications;

/// <summary>
/// Where Airside will and will not send a webhook.
/// </summary>
/// <remarks>
/// <para>
/// A webhook lets a user make <em>this</em> server issue an HTTP request, and this
/// server is a bad one to have that power over: it holds the Docker socket and
/// shares a network with Caddy's admin API, which is unauthenticated and can load
/// configuration that executes commands.
/// </para>
/// <para>
/// Every case below is a real destination someone would reach for. The cloud
/// metadata address is the one that turns a notification feature into credential
/// theft, and the Docker bridge range is the one specific to how Airside is
/// deployed.
/// </para>
/// </remarks>
public class OutboundGuardTests
{
    private static OutboundVerdict Check(string address) => OutboundGuard.Check(IPAddress.Parse(address));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    public void LoopbackIsRefused(string address)
    {
        // Reaches Airside's own API and anything else bound locally that was
        // deliberately not exposed.
        var verdict = Check(address);

        Assert.False(verdict.IsAllowed);
        Assert.Equal("loopback", verdict.Reason);
    }

    [Fact]
    public void TheCloudMetadataAddressIsRefused()
    {
        // The single worst destination: a request here returns IAM credentials on
        // AWS and an access token on GCP and Azure.
        var verdict = Check("169.254.169.254");

        Assert.False(verdict.IsAllowed);
        Assert.Equal("link_local", verdict.Reason);
        Assert.Contains("metadata", verdict.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    public void PrivateRangesAreRefused(string address)
    {
        var verdict = Check(address);

        Assert.False(verdict.IsAllowed);
        Assert.Equal("private_network", verdict.Reason);
    }

    [Fact]
    public void TheDockerBridgePoolIsRefused()
    {
        // Airside's own installer configures 172.16.0.0/12 as Docker's address
        // pool, so this range is where airside-proxy and every workload lives — and
        // the proxy's admin API on 2019 is unauthenticated.
        Assert.False(Check("172.18.0.2").IsAllowed);
        Assert.False(Check("172.20.5.5").IsAllowed);
    }

    [Theory]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    public void AddressesJustOutsideThePrivateRangeAreAllowed(string address)
    {
        // 172.16/12 is not 172/8, and a guard that treated it as such would refuse
        // a large block of perfectly ordinary public addresses.
        Assert.True(Check(address).IsAllowed);
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    public void IpV6PrivateAndLinkLocalAreRefused(string address) =>
        Assert.False(Check(address).IsAllowed);

    [Fact]
    public void AnIpV4AddressWrappedAsIpV6IsJudgedAsIpV4()
    {
        // ::ffff:127.0.0.1 is loopback. Checking only the IPv6 rules against it
        // would let it through, and it is exactly the form a determined caller
        // would reach for.
        Assert.False(OutboundGuard.Check(IPAddress.Parse("::ffff:127.0.0.1")).IsAllowed);
        Assert.False(OutboundGuard.Check(IPAddress.Parse("::ffff:169.254.169.254")).IsAllowed);
        Assert.False(OutboundGuard.Check(IPAddress.Parse("::ffff:10.0.0.1")).IsAllowed);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    public void ReservedAndCarrierGradeRangesAreRefused(string address) =>
        Assert.False(Check(address).IsAllowed);

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("140.82.121.4")]
    [InlineData("2606:4700::1111")]
    public void OrdinaryPublicAddressesAreAllowed(string address)
    {
        // The feature has to work. A guard that refused everything would be safe
        // and useless.
        Assert.True(Check(address).IsAllowed);
    }
}

/// <summary>
/// The escape hatch, and what it deliberately does not open.
/// </summary>
/// <remarks>
/// An operator running a receiver on their own network has to be able to say so.
/// What that must not do is re-open the two destinations that are never a
/// legitimate webhook target — the cloud metadata service and this host itself.
/// An earlier version had one switch for all of it, which made "I have an
/// internal receiver" also mean "and you may read my IAM credentials".
/// </remarks>
public class OutboundGuardEscapeHatchTests
{
    private static OutboundVerdict Check(string address, bool allowPrivate) =>
        OutboundGuard.Check(IPAddress.Parse(address), allowPrivate);

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("172.18.0.2")]
    [InlineData("fd12:3456::1")]
    public void PrivateNetworksOpenWhenAsked(string address)
    {
        Assert.False(Check(address, allowPrivate: false).IsAllowed);
        Assert.True(Check(address, allowPrivate: true).IsAllowed);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:169.254.169.254")]
    public void MetadataAndLoopbackStayRefusedRegardless(string address)
    {
        // The whole reason the switch is split. Neither of these is ever a
        // webhook receiver, and both are catastrophic to reach.
        Assert.False(Check(address, allowPrivate: false).IsAllowed);
        Assert.False(Check(address, allowPrivate: true).IsAllowed);
    }
}
