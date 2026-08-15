using Airside.Core.Common;

namespace Airside.Core.Naming;

/// <summary>
/// Derives every Docker object name from a validated <see cref="Slug"/>.
/// </summary>
/// <remarks>
/// Names are derived, never user-supplied. Because <see cref="Slug"/> cannot hold
/// an invalid value, nothing produced here can contain a character that would
/// mean something to a shell, a path parser, or a proxy matcher — which is what
/// makes "never build shell commands from user input" structural rather than a
/// review checklist item.
/// </remarks>
public static class AirsideNames
{
    public static string DatabaseContainer(Slug slug) => $"airside-db-{slug.Value}";

    public static string ApplicationContainer(Slug slug, Guid deploymentId) =>
        $"airside-app-{slug.Value}-{ShortId(deploymentId)}";

    public static string Volume(Slug slug, string purpose) => $"airside-vol-{slug.Value}-{purpose}";

    public static string DatabaseNetwork(Slug slug) => $"airside-net-db-{slug.Value}";

    public static string ApplicationNetwork(Slug slug) => $"airside-net-app-{slug.Value}";

    /// <summary>The network shared by the API, the control-plane store, and the proxy admin API.</summary>
    public const string InternalNetwork = "airside-internal";

    public static string ShortId(Guid id) => id.ToString("N")[..8];
}
