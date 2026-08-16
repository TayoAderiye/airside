namespace Airside.Api.Infrastructure;

/// <summary>
/// The banner printed on startup.
/// </summary>
/// <remarks>
/// <para>
/// Written to stdout rather than through the logger. This is the first thing an
/// operator sees in <c>docker logs airside-api</c> after an install, and a
/// structured log line would be shipped, indexed, and rendered as JSON — which is
/// not what a welcome is for.
/// </para>
/// <para>
/// Plain ASCII on purpose. Box-drawing and emoji render inconsistently over SSH,
/// in CI log viewers, and on a serial console, and the one place this has to be
/// legible is a freshly installed box being watched over a shaky connection.
/// </para>
/// </remarks>
internal static class AirsideBanner
{
    public static void Write(string version, string storeProvider, string? dashboardUrl = null)
    {
        Console.WriteLine();
        Console.WriteLine(@"                       __|__");
        Console.WriteLine(@"              *---------(_)---------*");
        Console.WriteLine();
        Console.WriteLine(@"                 A I R S I D E");
        Console.WriteLine(@"           control plane for one server");
        Console.WriteLine();
        Console.WriteLine($"           version {version}  |  store {storeProvider}");

        if (!string.IsNullOrWhiteSpace(dashboardUrl))
        {
            Console.WriteLine($"           {dashboardUrl}");
        }

        Console.WriteLine();
    }
}
