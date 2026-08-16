using Airside.Core.Naming;

namespace Airside.Core.Proxy;

/// <summary>
/// The proxy route for the dashboard's own hostname.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard is two containers behind one hostname: a Next server holding
/// the UI, and the API. They are split so the UI can be released without cutting
/// a platform release, which means the boundary between them is a path prefix
/// rather than a port an operator has to know about.
/// </para>
/// <para>
/// Defined once, here, because more than one caller builds this route —
/// the endpoint that sets the domain, and anything that later has to restore it.
/// Two hand-written copies would agree right up until someone added a path to
/// one of them, and the failure that produces is the dashboard fetching its own
/// HTML in answer to an API call.
/// </para>
/// </remarks>
public static class DashboardRoute
{
    /// <summary>Matches the API container's <c>ASPNETCORE_URLS</c>.</summary>
    public const int ApiPort = 8080;

    /// <summary>Matches the UI image's <c>PORT</c>.</summary>
    public const int UiPort = 3000;

    /// <summary>
    /// Paths on the dashboard hostname that belong to the API.
    /// </summary>
    /// <remarks>
    /// Caddy path matchers, so the trailing <c>/*</c> is load-bearing. Everything
    /// not named here reaches the UI, which is the safer default of the two: a
    /// missed UI path is a 404 from Next, whereas a missed API path would be
    /// answered with the dashboard's HTML and parsed as JSON.
    /// </remarks>
    public static IReadOnlyList<string> ApiPaths { get; } = ["/api/*", "/openapi/*", "/health"];

    /// <summary>The route to install for <paramref name="hostname"/>.</summary>
    public static RouteSpec For(string hostname, HstsPolicy? hsts = null) =>
        new(
            hostname,
            new UpstreamTarget(AirsideLabels.SystemContainers.Ui, UiPort),
            Hsts: hsts,
            PathOverrides:
            [
                new PathUpstream(
                    ApiPaths,
                    new UpstreamTarget(AirsideLabels.SystemContainers.Api, ApiPort)),
            ]);
}
