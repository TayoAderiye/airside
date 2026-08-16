using Airside.Core.Common;
using Airside.Core.Containers;

namespace Airside.Core.Databases;

/// <summary>
/// Everything engine-specific, behind one interface.
/// </summary>
/// <remarks>
/// Implementations are the only place in Airside that knows Postgres from Redis.
/// Callers ask <see cref="Capabilities"/> and act on the answer.
/// </remarks>
public interface IDatabaseEngine
{
    DatabaseEngineKind Kind { get; }

    DatabaseEngineCapabilities Capabilities { get; }

    /// <summary>Versions Airside will provision, newest first.</summary>
    IReadOnlyList<string> SupportedVersions { get; }

    /// <summary>
    /// Resolves a version and variant to an image tag.
    /// </summary>
    /// <remarks>
    /// Never consulted when the caller supplied a custom image: that bypasses
    /// variant resolution entirely. Digest pinning happens at provision time, and
    /// everything afterwards resolves by digest rather than by this tag.
    /// </remarks>
    ImageReference ResolveImage(string version, ImageVariant variant);

    /// <summary>Rejects engine-inapplicable fields — a database name for Redis, a missing maxmemory.</summary>
    Result Validate(DatabaseProvisionSpec spec);

    /// <summary>
    /// Computes the recommended <c>maxmemory</c> for a container limit, with the
    /// reasoning attached so the UI can explain the number rather than assert it.
    /// Returns null for engines where the setting does not apply.
    /// </summary>
    MaxMemoryRecommendation? RecommendMaxMemory(long containerMemoryBytes, bool persistenceEnabled);

    ContainerSpec BuildContainerSpec(DatabaseProvisionSpec spec, ProvisionContext context);

    ConnectionDetails BuildConnectionDetails(DatabaseEndpoint endpoint, DatabaseCredentialValue credential);

    /// <summary>
    /// Renders the environment variables injected into an attached application.
    /// Keys are prefixed per attachment; Redis yields no name or user key.
    /// </summary>
    IReadOnlyList<EnvironmentEntry> BuildInjectedEnvironment(string keyPrefix, ConnectionDetails details);

    Task<DatabaseProbeResult> ProbeAsync(DatabaseEndpoint endpoint, DatabaseCredentialValue credential, CancellationToken ct);

    /// <summary>
    /// Produces a backup into <paramref name="destination"/>. Logical for the SQL
    /// engines and MongoDB; a BGSAVE-and-copy for Redis, which polls
    /// <c>rdb_bgsave_in_progress</c> via <c>INFO persistence</c> before copying.
    /// </summary>
    Task<BackupArtifact> BackupAsync(BackupOperation operation, Stream destination, CancellationToken ct);

    /// <summary>
    /// Restores from <paramref name="source"/>. When
    /// <see cref="DatabaseEngineCapabilities.RequiresStopForRestore"/> is set, the
    /// caller has already stopped the container and will restart it afterwards.
    /// </summary>
    Task RestoreAsync(RestoreOperation operation, Stream source, CancellationToken ct);

    Task RotatePasswordAsync(DatabaseEndpoint endpoint, DatabaseCredentialValue current, Secret replacement, CancellationToken ct);
}

/// <summary>Resolves the implementation for an engine kind.</summary>
public interface IDatabaseEngineRegistry
{
    IDatabaseEngine Get(DatabaseEngineKind kind);

    IReadOnlyList<IDatabaseEngine> All { get; }
}
