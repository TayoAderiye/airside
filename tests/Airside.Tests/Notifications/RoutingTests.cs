using Airside.Core.Notifications;

namespace Airside.Tests.Notifications;

/// <summary>
/// Which notifications reach which channel.
/// </summary>
/// <remarks>
/// The failure worth designing against here is silence: a rule that accidentally
/// matches nothing leaves a channel quiet, and nobody finds out until the incident
/// it was meant to report. So the defaults, the precedence, and the reason given
/// for every refusal are all under test — not just the happy path.
/// </remarks>
public class RoutingTests
{
    private static RouteDecision Evaluate(
        NotificationRoute route,
        string? code = "domain.certificate_expiring",
        string? kind = "domain",
        Guid? id = null,
        NotificationSeverityLevel severity = NotificationSeverityLevel.Warning,
        NotificationSeverityLevel minimum = NotificationSeverityLevel.Info) =>
        NotificationRouter.Evaluate(route, severity, minimum, code, kind, id);

    [Fact]
    public void AnEmptyRouteSendsEverything()
    {
        // The default, and the setting every channel created before routing
        // existed carries. If empty meant "nothing", adding this feature would
        // have silently stopped every channel already configured.
        Assert.True(NotificationRoute.All.IsPassThrough);
        Assert.True(Evaluate(NotificationRoute.All).Matches);
        Assert.True(Evaluate(NotificationRoute.All, code: null, kind: null).Matches);
    }

    [Fact]
    public void AnIncludeListNarrowsToWhatIsListed()
    {
        var route = new NotificationRoute { IncludeCodes = ["domain"] };

        Assert.True(Evaluate(route, code: "domain.certificate_expiring").Matches);
        Assert.False(Evaluate(route, code: "backup.failed").Matches);
    }

    [Fact]
    public void PrefixesMatchOnSegmentBoundaries()
    {
        // "domain" must not match "domainless". A bare StartsWith would, and the
        // surprise only shows up as a channel receiving something nobody asked it
        // to.
        Assert.True(NotificationRouter.Matches("domain.certificate_expiring", "domain"));
        Assert.True(NotificationRouter.Matches("domain", "domain"));
        Assert.False(NotificationRouter.Matches("domainless.thing", "domain"));
        Assert.False(NotificationRouter.Matches(null, "domain"));
    }

    [Theory]
    [InlineData("domain.")]
    [InlineData("domain.*")]
    [InlineData("domain*")]
    public void TheFormsPeopleActuallyTypeAllWork(string prefix)
    {
        // Someone writing a filter will reach for a trailing dot or a star. All
        // three mean the same thing, and rejecting two of them would produce a
        // rule that saves and silently matches nothing.
        Assert.True(NotificationRouter.Matches("domain.certificate_expiring", prefix));
    }

    [Fact]
    public void ExcludeBeatsInclude()
    {
        // "Everything under domain, except the expiry warnings" is a sentence
        // people say. Making exclude win means the two lists never have to be read
        // in order to know the answer.
        var route = new NotificationRoute
        {
            IncludeCodes = ["domain"],
            ExcludeCodes = ["domain.certificate_expiring"],
        };

        Assert.False(Evaluate(route, code: "domain.certificate_expiring").Matches);
        Assert.True(Evaluate(route, code: "domain.failed").Matches);
    }

    [Fact]
    public void SeverityIsStillCheckedAndIsCheckedFirst()
    {
        var route = new NotificationRoute { IncludeCodes = ["domain"] };

        var decision = Evaluate(
            route,
            severity: NotificationSeverityLevel.Info,
            minimum: NotificationSeverityLevel.Error);

        Assert.False(decision.Matches);
        Assert.Contains("minimum severity", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceKindNarrowsToOneSortOfThing()
    {
        var route = new NotificationRoute { IncludeResourceKinds = ["database"] };

        Assert.True(Evaluate(route, kind: "database").Matches);
        Assert.False(Evaluate(route, kind: "domain").Matches);

        // A notification about nothing in particular cannot satisfy a rule about
        // a kind of thing.
        Assert.False(Evaluate(route, kind: null).Matches);
    }

    [Fact]
    public void ASingleNoisyResourceCanBeExcluded()
    {
        // The "stop paging me about staging" case, which is the second thing
        // anyone asks for after severity.
        var staging = Guid.CreateVersion7();
        var production = Guid.CreateVersion7();
        var route = new NotificationRoute { ExcludeResourceIds = [staging] };

        Assert.False(Evaluate(route, id: staging).Matches);
        Assert.True(Evaluate(route, id: production).Matches);
    }

    [Fact]
    public void AnExcludedResourceIsRefusedEvenWhenItsCodeIsIncluded()
    {
        var staging = Guid.CreateVersion7();

        var route = new NotificationRoute
        {
            IncludeCodes = ["domain"],
            ExcludeResourceIds = [staging],
        };

        Assert.False(Evaluate(route, code: "domain.certificate_expiring", id: staging).Matches);
    }

    [Fact]
    public void EveryRefusalSaysWhy()
    {
        // Recorded on the skipped delivery, so "why did Slack not get this" has an
        // answer that is not "read the rules and work it out".
        var route = new NotificationRoute { IncludeCodes = ["backup"] };

        var decision = Evaluate(route, code: "domain.certificate_expiring");

        Assert.False(decision.Matches);
        Assert.Contains("domain.certificate_expiring", decision.Reason!, StringComparison.Ordinal);
        Assert.Contains("does not match", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesSurviveARoundTripThroughStorage()
    {
        var original = new NotificationRoute
        {
            IncludeCodes = ["domain", "backup"],
            ExcludeCodes = ["domain.awaiting_certificate"],
            IncludeResourceKinds = ["domain"],
            ExcludeResourceIds = [Guid.CreateVersion7()],
        };

        var restored = NotificationRoute.FromJson(original.ToJson());

        Assert.Equal(original.IncludeCodes, restored.IncludeCodes);
        Assert.Equal(original.ExcludeCodes, restored.ExcludeCodes);
        Assert.Equal(original.ExcludeResourceIds, restored.ExcludeResourceIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    public void UnreadableRulesFallBackToSendingEverything(string? json)
    {
        // Noisy rather than silent. A rule that cannot be parsed must not leave a
        // channel that looks configured and never fires.
        Assert.True(NotificationRoute.FromJson(json).IsPassThrough);
    }
}
