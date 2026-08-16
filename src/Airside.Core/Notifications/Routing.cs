using System.Text.Json;
using System.Text.Json.Serialization;
using Airside.Core.Operations;

namespace Airside.Core.Notifications;

/// <summary>
/// Which notifications a channel wants, beyond severity.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not an expression language. The questions people actually have
/// are "send certificate problems to the ops channel" and "do not page me about
/// the staging application", and both are lists. A DSL would answer them too,
/// along with a class of rule nobody can debug at three in the morning.
/// </para>
/// <para>
/// The failure this shape guards against is silence. A rule that accidentally
/// matches nothing leaves a channel quiet, and nobody notices until the incident
/// it was meant to report — which is worse than no filtering at all. So empty
/// means everything, excludes are separate from includes rather than a single
/// ordered list, and every filtered notification records <em>why</em> it was
/// filtered.
/// </para>
/// </remarks>
public sealed record NotificationRoute
{
    /// <summary>
    /// Code prefixes to send. Empty means every code.
    /// </summary>
    /// <remarks>
    /// Empty is "everything" rather than "nothing" on purpose: it is the default
    /// for a channel created before routing existed, and the alternative would
    /// silently stop every one of them.
    /// </remarks>
    public IReadOnlyList<string> IncludeCodes { get; init; } = [];

    /// <summary>
    /// Code prefixes never to send, whatever else matches.
    /// </summary>
    /// <remarks>
    /// Excludes beat includes. "Everything under domain, except the expiry
    /// warnings" is a sentence people say; the reverse is not, and making exclude
    /// win means the two lists never need to be read in order.
    /// </remarks>
    public IReadOnlyList<string> ExcludeCodes { get; init; } = [];

    /// <summary>Resource kinds to send — <c>domain</c>, <c>application</c>, <c>database</c>. Empty means all.</summary>
    public IReadOnlyList<string> IncludeResourceKinds { get; init; } = [];

    /// <summary>Specific resources to send about. Empty means all of the kinds above.</summary>
    public IReadOnlyList<Guid> IncludeResourceIds { get; init; } = [];

    public IReadOnlyList<Guid> ExcludeResourceIds { get; init; } = [];

    /// <summary>True when this route sends everything it is offered.</summary>
    [JsonIgnore]
    public bool IsPassThrough =>
        IncludeCodes.Count == 0
        && ExcludeCodes.Count == 0
        && IncludeResourceKinds.Count == 0
        && IncludeResourceIds.Count == 0
        && ExcludeResourceIds.Count == 0;

    public static NotificationRoute All { get; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static NotificationRoute FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return All;
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationRoute>(json, Json) ?? All;
        }
        catch (JsonException)
        {
            // A rule that cannot be read must not silence the channel. Falling
            // back to "send everything" is noisy; falling back to "send nothing"
            // is a channel that looks configured and never fires.
            return All;
        }
    }
}

/// <param name="Reason">
/// Why a notification was not sent, in the channel's own terms. Recorded on the
/// skipped delivery so "why did Slack not get this" has an answer that is not
/// "look at the rules and work it out".
/// </param>
public sealed record RouteDecision(bool Matches, string? Reason = null)
{
    public static RouteDecision Send { get; } = new(true);

    public static RouteDecision Skip(string reason) => new(false, reason);
}

/// <summary>Decides whether one notification belongs on one channel.</summary>
public static class NotificationRouter
{
    /// <summary>
    /// Evaluates severity and the route together, and says why when it says no.
    /// </summary>
    /// <remarks>
    /// One function rather than a severity check in the dispatcher and a route
    /// check here, so there is a single answer to "would this be delivered" — the
    /// same one the preview endpoint gives before anyone relies on the rule.
    /// </remarks>
    public static RouteDecision Evaluate(
        NotificationRoute route,
        NotificationSeverityLevel severity,
        NotificationSeverityLevel minimum,
        string? code,
        string? resourceKind,
        Guid? resourceId)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (severity < minimum)
        {
            return RouteDecision.Skip(
                $"below the channel's minimum severity ({minimum.ToString().ToLowerInvariant()})");
        }

        // Excludes first, and they win. Reading them last would make the result
        // depend on the order the two lists happen to be evaluated in, which is
        // exactly the ambiguity that makes filter rules hard to reason about.
        if (resourceId is { } id && route.ExcludeResourceIds.Contains(id))
        {
            return RouteDecision.Skip("the resource is excluded from this channel");
        }

        if (route.ExcludeCodes.Count > 0 && MatchesAny(code, route.ExcludeCodes, out var excluded))
        {
            return RouteDecision.Skip($"'{excluded}' is excluded from this channel");
        }

        if (route.IncludeCodes.Count > 0 && !MatchesAny(code, route.IncludeCodes, out _))
        {
            return RouteDecision.Skip(
                code is null
                    ? "this channel only sends notifications with a code, and this one has none"
                    : $"'{code}' does not match any code this channel sends");
        }

        if (route.IncludeResourceKinds.Count > 0
            && (resourceKind is null
                || !route.IncludeResourceKinds.Contains(resourceKind, StringComparer.OrdinalIgnoreCase)))
        {
            return RouteDecision.Skip(
                resourceKind is null
                    ? "this channel only sends notifications about a resource, and this one is not about one"
                    : $"this channel does not send notifications about a {resourceKind}");
        }

        if (route.IncludeResourceIds.Count > 0 && (resourceId is null || !route.IncludeResourceIds.Contains(resourceId.Value)))
        {
            return RouteDecision.Skip("this channel only sends notifications about specific resources");
        }

        return RouteDecision.Send;
    }

    /// <summary>
    /// Matches a code against a prefix, on segment boundaries.
    /// </summary>
    /// <remarks>
    /// <c>domain</c> matches <c>domain.certificate_expiring</c> but not
    /// <c>domainless.thing</c>. A bare <c>StartsWith</c> would match both, and the
    /// surprise would only surface as a channel receiving something nobody asked
    /// it to.
    /// </remarks>
    public static bool Matches(string? code, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (code is null)
        {
            return false;
        }

        var normalised = prefix.TrimEnd('.', '*');

        if (normalised.Length == 0)
        {
            return true;
        }

        return code.Equals(normalised, StringComparison.OrdinalIgnoreCase)
            || code.StartsWith(normalised + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAny(string? code, IReadOnlyList<string> prefixes, out string? matched)
    {
        foreach (var prefix in prefixes)
        {
            if (Matches(code, prefix))
            {
                matched = prefix;
                return true;
            }
        }

        matched = null;
        return false;
    }
}

/// <summary>
/// Severity as the router sees it.
/// </summary>
/// <remarks>
/// Mirrors the persisted enum rather than referencing it, so the routing rules
/// stay in <c>Airside.Core</c> and do not pull the data layer in behind them.
/// </remarks>
public enum NotificationSeverityLevel
{
    Info,
    Warning,
    Error,
}
