using Airside.Core.Common;

namespace Airside.Core.Containers;

/// <param name="Password">
/// A password or, far more often, an access token. Held as a <see cref="Secret"/>
/// so a logged or serialised pull request cannot leak it — Docker's own client
/// puts this in a header, and a progress line that echoed the request would put a
/// registry token in the deployment log a user can read.
/// </param>
public sealed record RegistryAuth(string Registry, string Username, Secret Password);

/// <summary>
/// Finds the credential, if any, for the registry an image lives on.
/// </summary>
/// <remarks>
/// Resolution is by registry host rather than by workload. A token for
/// <c>ghcr.io</c> works for every image on it, and asking an operator to attach
/// the same credential to each application separately produces copies that then
/// have to be rotated separately — which is how one of them ends up stale.
/// </remarks>
public interface IRegistryCredentialSource
{
    /// <summary>Returns null when the registry is public or has no stored credential.</summary>
    Task<RegistryAuth?> ResolveAsync(ImageReference image, CancellationToken ct);
}

/// <summary>
/// Works out which registry an image reference names.
/// </summary>
/// <remarks>
/// <para>
/// Not simply "the part before the first slash". Docker's rule is that the first
/// path component is a registry only if it looks like a host — it contains a dot
/// or a colon, or is exactly <c>localhost</c>. Everything else is a Docker Hub
/// namespace.
/// </para>
/// <para>
/// The distinction is load-bearing and easy to get backwards. <c>myorg/app</c> is
/// an image on Docker Hub owned by <c>myorg</c>, while <c>myorg.io/app</c> is an
/// image on a registry called <c>myorg.io</c>. Getting it wrong sends a private
/// registry's token to Docker Hub, or looks for a Docker Hub credential under a
/// registry name that will never match.
/// </para>
/// </remarks>
public static class RegistryHost
{
    /// <summary>
    /// What Docker Hub is called when it is named explicitly.
    /// </summary>
    /// <remarks>
    /// Credentials for Hub are stored under this so that <c>nginx</c>,
    /// <c>library/nginx</c>, and <c>docker.io/library/nginx</c> all resolve to the
    /// same entry rather than three that drift apart.
    /// </remarks>
    public const string DockerHub = "docker.io";

    public static string Of(ImageReference image)
    {
        ArgumentNullException.ThrowIfNull(image);

        return Of(image.Repository);
    }

    public static string Of(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return DockerHub;
        }

        var slash = repository.IndexOf('/', StringComparison.Ordinal);

        if (slash <= 0)
        {
            // A bare name such as "nginx" — Docker Hub's library namespace.
            return DockerHub;
        }

        var candidate = repository[..slash];

        // "index.docker.io" and "registry-1.docker.io" are the same registry under
        // different names, and a credential stored under one must satisfy the
        // other.
        if (candidate is "index.docker.io" or "registry-1.docker.io" or DockerHub)
        {
            return DockerHub;
        }

        return candidate.Contains('.', StringComparison.Ordinal)
            || candidate.Contains(':', StringComparison.Ordinal)
            || candidate == "localhost"
                ? candidate
                : DockerHub;
    }

    /// <summary>
    /// Normalises a registry as typed by a user into the form lookups use.
    /// </summary>
    /// <remarks>
    /// People paste <c>https://ghcr.io/</c> or <c>ghcr.io/myorg</c> when asked for
    /// a registry. Both are understood rather than rejected, because the
    /// alternative is a credential that is stored successfully and never matches
    /// anything, with nothing to say why.
    /// </remarks>
    public static string Normalise(string? registry)
    {
        if (string.IsNullOrWhiteSpace(registry))
        {
            return DockerHub;
        }

        var value = registry.Trim().ToLowerInvariant();

        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (value.StartsWith(scheme, StringComparison.Ordinal))
            {
                value = value[scheme.Length..];
            }
        }

        value = value.TrimEnd('/');

        var slash = value.IndexOf('/', StringComparison.Ordinal);

        if (slash > 0)
        {
            value = value[..slash];
        }

        return value is "index.docker.io" or "registry-1.docker.io" or "" ? DockerHub : value;
    }
}

/// <summary>
/// A registry refused a pull.
/// </summary>
/// <remarks>
/// Separate from the general runtime failure because the two send an operator in
/// opposite directions. "The container runtime is unreachable, check Docker" is
/// what a missing credential used to report, and it is exactly wrong: Docker is
/// fine, and the thing to fix is a token.
/// </remarks>
public sealed class RegistryAuthenticationException : ContainerRuntimeException
{
    public RegistryAuthenticationException()
    {
    }

    public RegistryAuthenticationException(string message)
        : base(message)
    {
    }

    public RegistryAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
