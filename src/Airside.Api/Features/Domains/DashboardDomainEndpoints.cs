using System.Security.Claims;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Domains;
using Airside.Core.Proxy;
using Airside.Core.Security;
using Airside.Data;
using Airside.Runtime.Dns;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Domains;

/// <param name="ConfirmHostname">
/// Must equal the new hostname exactly. This is the one operation in Airside that
/// can lock an administrator out of Airside.
/// </param>
public sealed record SetDashboardDomainRequest(string Hostname, string ConfirmHostname);

public sealed record DashboardDomainDto(
    string? Hostname,
    string? PreviousHostname,
    DateTimeOffset? PreviousValidUntil,
    bool AwaitingDomain,
    IReadOnlyList<PreflightCheckDto> Checks);

/// <summary>
/// The hostname the dashboard itself is served on.
/// </summary>
/// <remarks>
/// <para>
/// Handled apart from application domains because the failure mode is different
/// in kind. Getting an application's domain wrong breaks that application;
/// getting this one wrong locks the administrator out of the tool they would use
/// to fix it, permanently, from a single mistyped record.
/// </para>
/// <para>
/// So: DNS is verified <em>before</em> the switch rather than after, the change is
/// confirmed by typing the hostname, and the previous route stays live for a
/// grace period so the old URL keeps working while DNS propagates. If all of that
/// still fails, <c>airside domain reset</c> on the host clears it and restores
/// access over the IP address.
/// </para>
/// </remarks>
internal static class DashboardDomainEndpoints
{
    /// <summary>
    /// How long the previous hostname keeps working after a change.
    /// </summary>
    /// <remarks>
    /// Long enough to cover DNS propagation and to notice a mistake while still
    /// holding a working URL. Shorter would defeat the point; much longer would
    /// leave two hostnames serving the dashboard for no reason.
    /// </remarks>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    public static IEndpointRouteBuilder MapDashboardDomainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/settings/dashboard-domain", GetAsync)
            .WithTags("Settings")
            .RequirePermission(Permissions.ServerManage);

        app.MapPut("/api/v1/settings/dashboard-domain", SetAsync)
            .WithTags("Settings")
            .RequirePermission(Permissions.ServerManage)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        return app;
    }

    private static async Task<Ok<DashboardDomainDto>> GetAsync(
        AirsideDbContext db,
        CancellationToken ct)
    {
        var settings = await db.InstanceSettings.AsNoTracking().FirstAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new DashboardDomainDto(
            settings.DashboardDomain,
            settings.PreviousDashboardDomain,
            settings.PreviousDashboardDomainUntil is null
                ? null
                : new DateTimeOffset(settings.PreviousDashboardDomainUntil.Value, TimeSpan.Zero),
            settings.AwaitingDomain,
            []));
    }

    private static async Task<Results<Ok<DashboardDomainDto>, ProblemHttpResult>> SetAsync(
        SetDashboardDomainRequest request,
        AirsideDbContext db,
        IDomainPreflight preflight,
        IProxyManager proxy,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!PublicSuffixList.TryNormalise(request.Hostname, out var hostname, out _))
        {
            return new Error(ErrorCodes.ValidationFailed, "That is not a valid hostname.").ToProblem();
        }

        if (!string.Equals(request.ConfirmHostname?.Trim().ToLowerInvariant(), hostname, StringComparison.Ordinal))
        {
            return new Error(
                "settings.dashboard_domain_confirmation_required",
                $"Type '{hostname}' to confirm. This changes the address you are currently signed in "
                + "through. If the hostname does not resolve to this server you will not be able to reach "
                + "Airside at the new address, and the only way back is to run 'airside domain reset' on "
                + "the host itself.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["confirmField"] = "confirmHostname",
                    ["expected"] = hostname,
                }).ToProblem();
        }

        if (await db.Domains.AnyAsync(d => d.Hostname == hostname, ct).ConfigureAwait(false))
        {
            return new Error(
                "settings.dashboard_domain_in_use",
                $"'{hostname}' is already routed to an application. Detach it first.").ToProblem();
        }

        // Verified before the switch, not after. Checking afterwards means finding
        // out it was wrong from a browser that can no longer reach anything.
        var report = await preflight
            .RunAsync(new PreflightRequest(hostname, TlsMode.Automatic), ct)
            .ConfigureAwait(false);

        if (report.Blocks)
        {
            var first = report.Blocking.First();

            return new Error(
                first.Id,
                $"The dashboard domain was not changed: {first.Summary}",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["found"] = first.Found,
                    ["expected"] = first.Expected,
                    ["remedy"] = first.Remedy,
                    ["checks"] = PreflightReportDto.From(report).Checks,
                }).ToProblem();
        }

        var settings = await db.InstanceSettings.FirstAsync(ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previous = settings.DashboardDomain;

        settings.PreviousDashboardDomain = previous;
        settings.PreviousDashboardDomainUntil = previous is null ? null : now.Add(GracePeriod);
        settings.DashboardDomain = hostname;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // The new route goes up before the old one is touched, and the old one is
        // left alone entirely — it expires on the grace period rather than being
        // withdrawn here, so the URL the administrator is currently using keeps
        // working while DNS propagates.
        await proxy.UpsertRouteAsync(DashboardRoute.For(hostname), ct).ConfigureAwait(false);

        // Withdrawn now rather than at the next reconciliation pass. Until it goes
        // the dashboard also answers on the bare IP and on any other hostname
        // pointed at this host, which is exactly the exposure setting a dashboard
        // domain is meant to close.
        await proxy.RemoveFallbackRouteAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "settings.dashboard_domain_changed",
            Result = AuditResult.Success,
            UserId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null,
            ResourceKind = "settings",
            ResourceSlugSnapshot = hostname,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["previous"] = previous },
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(new DashboardDomainDto(
            settings.DashboardDomain,
            settings.PreviousDashboardDomain,
            settings.PreviousDashboardDomainUntil is null
                ? null
                : new DateTimeOffset(settings.PreviousDashboardDomainUntil.Value, TimeSpan.Zero),
            settings.AwaitingDomain,
            [.. report.Checks.Select(PreflightCheckDto.From)]));
    }
}
