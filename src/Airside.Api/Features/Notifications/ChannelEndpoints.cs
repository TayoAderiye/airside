using System.Security.Claims;
using System.Text.Json;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Notifications;
using Airside.Core.Operations;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Notifications;

/// <param name="Endpoint">
/// A URL for webhook, or a recipient address for email. For Slack this is ignored
/// — the incoming-webhook URL goes in <paramref name="Secret"/>, because the URL
/// <em>is</em> the credential.
/// </param>
/// <param name="Secret">
/// A webhook signing key, a Slack incoming-webhook URL, or an SMTP password.
/// Stored encrypted and never returned.
/// </param>
public sealed record SaveChannelRequest(
    string Name,
    string Kind,
    string? Endpoint,
    string? Secret,
    string MinimumSeverity = "warning",
    bool Enabled = true,
    Dictionary<string, string>? Settings = null,
    NotificationRoute? Routing = null);

public sealed record ChannelDto(
    Guid Id,
    string Name,
    string Kind,
    string Endpoint,
    bool HasSecret,
    string MinimumSeverity,
    bool Enabled,
    DateTimeOffset? LastAttemptAt,
    bool? LastAttemptSucceeded,
    string? LastAttemptError,
    int ConsecutiveFailures,
    DateTimeOffset? MutedUntil,
    IReadOnlyDictionary<string, string> Settings,
    NotificationRoute Routing)
{
    public static ChannelDto From(NotificationChannel c)
    {
        ArgumentNullException.ThrowIfNull(c);

        var settings = string.IsNullOrWhiteSpace(c.SettingsJson)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(c.SettingsJson) ?? [];

        return new ChannelDto(
            c.Id,
            c.Name,
            c.Kind.ToString().ToLowerInvariant(),
            c.Endpoint,

            // Whether one exists, never the value — and no mask string either,
            // because a masked field in a form invites saving it back verbatim.
            !string.IsNullOrEmpty(c.EncryptedSecret),
            c.MinimumSeverity.ToString().ToLowerInvariant(),
            c.Enabled,
            c.LastAttemptAt is null ? null : new DateTimeOffset(c.LastAttemptAt.Value, TimeSpan.Zero),
            c.LastAttemptSucceeded,
            c.LastAttemptError,
            c.ConsecutiveFailures,
            c.MutedUntil is null ? null : new DateTimeOffset(c.MutedUntil.Value, TimeSpan.Zero),
            settings,
            NotificationRoute.FromJson(c.RoutingJson));
    }
}

public sealed record PreviewRouteRequest(NotificationRoute? Routing, string MinimumSeverity = "warning");

/// <param name="Warning">
/// Set when the rule matched nothing at all — the one outcome that looks like a
/// working configuration and is not.
/// </param>
public sealed record RoutePreviewDto(
    int Considered,
    int WouldSend,
    IReadOnlyList<RoutePreviewEntryDto> Notifications,
    string? Warning);

public sealed record RoutePreviewEntryDto(
    string Title,
    string? Code,
    string Severity,
    string? ResourceKind,
    bool WouldSend,
    string? Reason);

/// <param name="Warning">
/// Set when the channels between them would not deliver an error. Every channel
/// can be individually sensible and still leave no path for the thing that
/// matters.
/// </param>
public sealed record ChannelListDto(IReadOnlyList<ChannelDto> Channels, string? Warning);

internal static class ChannelEndpoints
{
    public static IEndpointRouteBuilder MapNotificationChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notification-channels").WithTags("Notifications");

        group.MapGet("/", ListAsync).RequirePermission(Permissions.ServerManage);
        group.MapPut("/", SaveAsync).RequirePermission(Permissions.ServerManage);
        group.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(Permissions.ServerManage);

        group.MapPost("/preview", PreviewAsync).RequirePermission(Permissions.ServerManage);

        group.MapPost("/{id:guid}/test", TestAsync)
            .RequirePermission(Permissions.ServerManage)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        return app;
    }

    private static async Task<Ok<ChannelListDto>> ListAsync(
        AirsideDbContext db,
        CancellationToken ct)
    {
        var rows = await db.NotificationChannels
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok(new ChannelListDto(
            [.. rows.Select(ChannelDto.From)],
            DescribeErrorCoverage(rows)));
    }

    /// <summary>
    /// Warns when nothing would receive an error.
    /// </summary>
    /// <remarks>
    /// The state routing rules make reachable and severity alone did not: every
    /// channel individually sensible, and between them no path for the one thing
    /// that matters. It looks exactly like a working configuration, and the way it
    /// is discovered is an incident nobody was told about — so it is said out loud
    /// on the page where channels are managed.
    /// </remarks>
    private static string? DescribeErrorCoverage(List<NotificationChannel> channels)
    {
        var enabled = channels.FindAll(c => c.Enabled);

        if (enabled.Count == 0)
        {
            return channels.Count == 0
                ? null
                : "Every notification channel is disabled, so nothing leaves Airside. Notifications are "
                  + "still recorded and visible here.";
        }

        // A synthetic error with no code and no resource: anything that filters
        // this out filters out the general "something is badly wrong" case.
        var reaches = enabled.Exists(c =>
            NotificationRouter.Evaluate(
                NotificationRoute.FromJson(c.RoutingJson),
                NotificationSeverityLevel.Error,
                ToLevel(c.MinimumSeverity),
                code: null,
                resourceKind: null,
                resourceId: null).Matches
            || NotificationRouter.Evaluate(
                NotificationRoute.FromJson(c.RoutingJson),
                NotificationSeverityLevel.Error,
                ToLevel(c.MinimumSeverity),
                code: "update.prepare_failed",
                resourceKind: null,
                resourceId: null).Matches);

        return reaches
            ? null
            : "No enabled channel would receive an error-level notification — every one of them filters "
              + "errors out. Notifications are still recorded here, but nothing will reach you.";
    }

    private static async Task<Results<Ok<ChannelDto>, ProblemHttpResult>> SaveAsync(
        SaveChannelRequest request,
        AirsideDbContext db,
        ISecretProtector protector,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<ChannelKind>(request.Kind, ignoreCase: true, out var kind))
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "Choose a channel kind: webhook, slack, or email.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "kind" }).ToProblem();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new Error(ErrorCodes.ValidationFailed, "A name is required.").ToProblem();
        }

        var validation = Validate(kind, request);

        if (validation is not null)
        {
            return validation.ToProblem();
        }

        var severity = Enum.TryParse<NotificationSeverity>(request.MinimumSeverity, ignoreCase: true, out var parsed)
            ? parsed
            : NotificationSeverity.Warning;

        var existing = await db.NotificationChannels
            .FirstOrDefaultAsync(c => c.Name == request.Name.Trim(), ct)
            .ConfigureAwait(false);

        var channel = existing ?? new NotificationChannel
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            CreatedByUserId = CurrentUserId(http),
        };

        channel.Kind = kind;
        channel.Endpoint = DisplayEndpoint(kind, request);
        channel.MinimumSeverity = severity;
        channel.Enabled = request.Enabled;
        channel.SettingsJson = JsonSerializer.Serialize(request.Settings ?? []);
        channel.RoutingJson = (request.Routing ?? NotificationRoute.All).ToJson();

        if (!string.IsNullOrWhiteSpace(request.Secret))
        {
            channel.EncryptedSecret = protector.Protect(new Secret(request.Secret));
        }

        // Changing the configuration clears the failure history: an operator who
        // has just fixed a URL should not have to wait out a mute imposed on the
        // old one.
        channel.ConsecutiveFailures = 0;
        channel.MutedUntil = null;
        channel.LastAttemptError = null;
        channel.LastAttemptSucceeded = null;

        if (existing is null)
        {
            db.NotificationChannels.Add(channel);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = existing is null ? "notification.channel_created" : "notification.channel_updated",
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "notification_channel",
            ResourceId = channel.Id,
            ResourceSlugSnapshot = channel.Name,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = kind.ToString(),

                // The display endpoint, which for Slack is only the host — the
                // token-bearing URL is the secret and does not belong in a table
                // designed to be readable.
                ["endpoint"] = channel.Endpoint,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(ChannelDto.From(channel));
    }

    /// <summary>
    /// Per-kind rules, checked before anything is stored.
    /// </summary>
    /// <remarks>
    /// The destination itself is not validated here beyond its shape: whether an
    /// address may be reached is decided at connect time by the outbound guard,
    /// because a hostname that resolves publicly now can resolve to loopback when
    /// the webhook actually fires.
    /// </remarks>
    private static Error? Validate(ChannelKind kind, SaveChannelRequest request)
    {
        switch (kind)
        {
            case ChannelKind.Webhook:
                if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var url)
                    || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
                {
                    return new Error(
                        ErrorCodes.ValidationFailed,
                        "A webhook needs an absolute http or https URL.",
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "endpoint" });
                }

                return null;

            case ChannelKind.Slack:
                if (!Uri.TryCreate(request.Secret, UriKind.Absolute, out var slack)
                    || slack.Scheme != Uri.UriSchemeHttps)
                {
                    return new Error(
                        ErrorCodes.ValidationFailed,
                        "Paste the Slack incoming-webhook URL into the secret field. It is the credential — "
                        + "anyone holding it can post to your channel — so Airside stores it encrypted "
                        + "rather than alongside the channel's name.",
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "secret" });
                }

                return null;

            case ChannelKind.Email:
                if (string.IsNullOrWhiteSpace(request.Endpoint) || !request.Endpoint.Contains('@', StringComparison.Ordinal))
                {
                    return new Error(
                        ErrorCodes.ValidationFailed,
                        "An email channel needs a recipient address.",
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "endpoint" });
                }

                if (request.Settings is null || !request.Settings.ContainsKey("host"))
                {
                    return new Error(
                        ErrorCodes.ValidationFailed,
                        "An email channel needs an SMTP host in settings, and usually a port, username, "
                        + "and from address as well.",
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "settings.host" });
                }

                return null;

            default:
                return new Error(ErrorCodes.ValidationFailed, $"Unsupported channel kind {kind}.");
        }
    }

    /// <summary>What is safe to show. For Slack that is the host and nothing else.</summary>
    private static string DisplayEndpoint(ChannelKind kind, SaveChannelRequest request)
    {
        if (kind != ChannelKind.Slack)
        {
            return request.Endpoint?.Trim() ?? string.Empty;
        }

        return Uri.TryCreate(request.Secret, UriKind.Absolute, out var slack)
            ? slack.Host
            : "hooks.slack.com";
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        AirsideDbContext db,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var channel = await db.NotificationChannels
            .FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);

        if (channel is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such channel.").ToProblem();
        }

        channel.DeletedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "notification.channel_deleted",
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "notification_channel",
            ResourceId = id,
            ResourceSlugSnapshot = channel.Name,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Sends a real message through the channel.
    /// </summary>
    /// <remarks>
    /// A genuine delivery rather than a connectivity check, because everything
    /// worth getting wrong is downstream of connecting: a Slack URL whose token
    /// was revoked, an SMTP account that authenticates but cannot send as the
    /// configured from-address, a webhook receiver that returns 200 for anything.
    /// The alternative is a channel that tests green and stays silent during an
    /// actual incident.
    /// </remarks>
    /// <summary>
    /// Shows which recent notifications a rule would have sent, before it is saved.
    /// </summary>
    /// <remarks>
    /// The reason this exists is the failure routing rules introduce: a rule that
    /// accidentally matches nothing leaves a channel quiet, and nobody finds out
    /// until the incident it was meant to report. Checking against real history —
    /// with the same function the dispatcher uses, so the answer cannot differ —
    /// turns that into something visible before it matters.
    /// </remarks>
    private static async Task<Ok<RoutePreviewDto>> PreviewAsync(
        PreviewRouteRequest request,
        AirsideDbContext db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var route = request.Routing ?? NotificationRoute.All;

        var minimum = Enum.TryParse<NotificationSeverity>(request.MinimumSeverity, ignoreCase: true, out var parsed)
            ? parsed
            : NotificationSeverity.Warning;

        // Real history rather than invented examples: a rule is usually wrong
        // about the codes this instance actually emits, not about the ones it
        // might.
        var recent = await db.Notifications
            .AsNoTracking()
            .OrderByDescending(n => n.LastSeenAt)
            .Take(100)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var results = recent.ConvertAll(n =>
        {
            var decision = NotificationRouter.Evaluate(
                route,
                ToLevel(n.Severity),
                ToLevel(minimum),
                n.Code,
                n.ResourceKind,
                n.ResourceId);

            return new RoutePreviewEntryDto(
                n.Title,
                n.Code,
                n.Severity.ToString().ToLowerInvariant(),
                n.ResourceKind,
                decision.Matches,
                decision.Reason);
        });

        var matched = results.Count(r => r.WouldSend);

        return TypedResults.Ok(new RoutePreviewDto(
            recent.Count,
            matched,
            results,

            // Said plainly, because a rule that matches nothing is the whole risk
            // and it looks identical to a rule that is simply waiting for its
            // first matching event.
            recent.Count > 0 && matched == 0
                ? "This rule matches none of the last " + recent.Count + " notifications. That may be "
                  + "correct if you are filtering for something that has not happened yet — but check the "
                  + "codes above against what you meant to select."
                : null));
    }

    private static NotificationSeverityLevel ToLevel(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => NotificationSeverityLevel.Error,
        NotificationSeverity.Warning => NotificationSeverityLevel.Warning,
        _ => NotificationSeverityLevel.Info,
    };

    private static async Task<Results<Ok<ChannelDto>, ProblemHttpResult>> TestAsync(
        Guid id,
        AirsideDbContext db,
        IEnumerable<INotificationTransport> transports,
        ISecretProtector protector,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var channel = await db.NotificationChannels
            .FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);

        if (channel is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such channel.").ToProblem();
        }

        var transport = transports.FirstOrDefault(t => t.Kind == channel.Kind);

        if (transport is null)
        {
            return new Error("notification.no_transport", $"No transport for {channel.Kind}.").ToProblem();
        }

        var settings = await db.InstanceSettings.AsNoTracking().FirstAsync(ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        Secret? secret = null;

        if (!string.IsNullOrEmpty(channel.EncryptedSecret))
        {
            var unprotected = protector.Unprotect(channel.EncryptedSecret);

            if (unprotected.IsFailure)
            {
                return new Error(
                    "notification.secret_unreadable",
                    "The stored secret could not be decrypted, which usually means the Data Protection key "
                    + "ring was replaced. Re-enter the channel's credentials.").ToProblem();
            }

            secret = unprotected.Value;
        }

        var channelSettings = string.IsNullOrWhiteSpace(channel.SettingsJson)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(channel.SettingsJson) ?? [];

        var outcome = await transport.SendAsync(
            new ChannelTarget(channel.Id, channel.Name, channel.Kind, channel.Endpoint, secret, channelSettings),
            new NotificationEnvelope(
                Guid.CreateVersion7(),
                NotificationLevel.Info,
                "Airside test notification",
                $"If you are reading this, '{channel.Name}' is working. No action is needed.",
                "notification.test",
                null,
                null,
                1,
                now,
                now,
                settings.InstanceName,
                settings.DashboardDomain is null ? null : $"https://{settings.DashboardDomain}/notifications"),
            ct).ConfigureAwait(false);

        channel.LastAttemptAt = now.UtcDateTime;
        channel.LastAttemptSucceeded = outcome.Succeeded;
        channel.LastAttemptError = outcome.Succeeded ? null : outcome.Detail;

        if (outcome.Succeeded)
        {
            channel.ConsecutiveFailures = 0;
            channel.MutedUntil = null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return outcome.Succeeded
            ? TypedResults.Ok(ChannelDto.From(channel))
            : new Error("notification.test_failed", outcome.Detail ?? "The test message was not delivered.")
                .ToProblem();
    }

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
