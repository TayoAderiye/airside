using Airside.Core.Databases;

namespace Airside.Data.Entities;

public enum ApplicationSourceKind
{
    /// <summary>An image that already exists; nothing is built.</summary>
    Image,

    /// <summary>A repository containing a Dockerfile.</summary>
    Git,

    /// <summary>A Dockerfile supplied inline, with no repository.</summary>
    Dockerfile,
}

public enum HealthCheckKind
{
    Http,
    Command,
}

/// <summary>A managed application container.</summary>
public class Application : Workload
{
    public ApplicationSourceKind SourceKind { get; set; }

    /// <summary>
    /// The image for an <see cref="ApplicationSourceKind.Image"/> source.
    /// </summary>
    /// <remarks>
    /// Named for what it is rather than mirroring <c>DatabaseInstance.ImageRef</c>:
    /// for a database that is the resolved engine image, for an application it is
    /// where the deployment starts from. They share a TPH table, and letting EF
    /// disambiguate the collision on its own produced an implicit
    /// <c>Application_ImageRef</c> column that nothing in the codebase named.
    /// </remarks>
    public string? SourceImageRef { get; set; }

    public Guid? RegistryCredentialId { get; set; }

    public string? GitRepositoryUrl { get; set; }

    public string? GitBranch { get; set; }

    public Guid? GitCredentialId { get; set; }

    /// <summary>Relative to the build context. Validated against traversal at the boundary.</summary>
    public string? DockerfilePath { get; set; }

    public string? BuildContextPath { get; set; }

    /// <summary>The Dockerfile itself for an inline source.</summary>
    public string? DockerfileContent { get; set; }

    /// <summary>The port the application listens on inside its container — the proxy upstream.</summary>
    public int ContainerPort { get; set; }

    /// <summary>
    /// How readiness is decided.
    /// </summary>
    /// <remarks>
    /// There is no "none". Zero-downtime deployment is defined as start-new,
    /// poll-health, swap-upstream, stop-old — without a health check that reduces
    /// to waiting a few seconds and hoping, and the model should not be able to
    /// express a promise the platform cannot keep.
    /// </remarks>
    public HealthCheckKind HealthCheckKind { get; set; }

    public string? HealthCheckPath { get; set; }

    public int? HealthCheckExpectedStatus { get; set; }

    public string? HealthCheckCommandJson { get; set; }

    public int HealthCheckIntervalSeconds { get; set; } = 10;

    public int HealthCheckTimeoutSeconds { get; set; } = 5;

    public int HealthCheckRetries { get; set; } = 3;

    public Guid? CurrentDeploymentId { get; set; }

    public ICollection<Deployment> Deployments { get; } = new List<Deployment>();

    public ICollection<EnvironmentVariable> EnvironmentVariables { get; } = new List<EnvironmentVariable>();

    public ICollection<DatabaseAttachment> Attachments { get; } = new List<DatabaseAttachment>();

    public Core.Workloads.ApplicationState CurrentState =>
        Enum.TryParse<Core.Workloads.ApplicationState>(State, out var parsed)
            ? parsed
            : Core.Workloads.ApplicationState.Failed;
}

public enum DeploymentStatus
{
    Queued,
    Building,
    Deploying,
    Succeeded,
    Failed,
    RolledBack,
}

public enum DeploymentTrigger
{
    Manual,
    Rollback,
    Api,
}

public class Deployment : Entity
{
    public Guid ApplicationId { get; set; }

    public Application Application { get; set; } = null!;

    /// <summary>Monotonic per application. Humans say "deployment 14", not a uuid.</summary>
    public int Number { get; set; }

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;

    public DeploymentTrigger TriggerKind { get; set; }

    public ApplicationSourceKind SourceKindSnapshot { get; set; }

    public string? CommitSha { get; set; }

    public string? CommitMessage { get; set; }

    public string? Branch { get; set; }

    public string? ImageRef { get; set; }

    /// <summary>
    /// What makes rollback a container start rather than a rebuild.
    /// </summary>
    /// <remarks>
    /// A tag can be overwritten by the next build; a digest cannot. Rolling back
    /// by tag would re-run whatever that tag points at now, which is not what the
    /// operator asked for.
    /// </remarks>
    public string? ImageDigest { get; set; }

    public string? ContainerId { get; set; }

    public Guid? JobId { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? DurationMs { get; set; }

    public bool IsCurrent { get; set; }

    public Guid? RolledBackFromDeploymentId { get; set; }

    public Guid? TriggeredByUserId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DeploymentLog? Log { get; set; }
}

/// <summary>
/// Build output, in its own table.
/// </summary>
/// <remarks>
/// Separate so listing deployments never loads megabytes of build log. Capped
/// with the head and tail retained: the useful parts of a failed build are the
/// first error and the last line, never the middle.
/// </remarks>
public class DeploymentLog
{
    public Guid DeploymentId { get; set; }

    public Deployment Deployment { get; set; } = null!;

    public string Content { get; set; } = string.Empty;

    public bool Truncated { get; set; }

    public int ByteCount { get; set; }
}

/// <summary>
/// A manually-entered environment variable.
/// </summary>
/// <remarks>
/// Only manual entries are rows. Attachment-injected keys are rendered at deploy
/// time from the attachment and the live credential, never stored — storing them
/// would mean a credential rotation leaves the running container holding a
/// password the UI no longer shows.
/// </remarks>
public class EnvironmentVariable : Entity
{
    public Guid ApplicationId { get; set; }

    public Application Application { get; set; } = null!;

    public string Key { get; set; } = string.Empty;

    /// <summary>Data Protection ciphertext when <see cref="IsSecret"/>, plaintext otherwise.</summary>
    public string Value { get; set; } = string.Empty;

    public bool IsSecret { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// An application's authorisation to reach a database.
/// </summary>
/// <remarks>
/// One record doing three jobs: it is the network authorisation, the credential
/// selection, and the source of the injected environment. Detach is a soft close
/// rather than a delete, because the answer to "who gave this app access to the
/// customer database" has to survive the access being removed.
/// </remarks>
public class DatabaseAttachment : Entity
{
    public Guid ApplicationId { get; set; }

    public Application Application { get; set; } = null!;

    public Guid DatabaseInstanceId { get; set; }

    public DatabaseInstance DatabaseInstance { get; set; } = null!;

    /// <summary>
    /// Defaults to the engine's own prefix. Editable so two attached databases
    /// cannot both claim <c>DATABASE_URL</c>.
    /// </summary>
    public string EnvKeyPrefix { get; set; } = string.Empty;

    public Guid CredentialId { get; set; }

    public DateTime AttachedAt { get; set; }

    public Guid? AttachedByUserId { get; set; }

    public DateTime? DetachedAt { get; set; }

    public Guid? DetachedByUserId { get; set; }
}

public enum CredentialKind
{
    SshKey,
    Token,
    UserPass,
}

/// <summary>A credential for a private repository or registry. Never returned in any response.</summary>
public class SourceCredential : Entity
{
    public string Name { get; set; } = string.Empty;

    public CredentialKind Kind { get; set; }

    public bool IsRegistry { get; set; }

    public string? Username { get; set; }

    /// <summary>Data Protection ciphertext.</summary>
    public string EncryptedSecret { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }

    public DateTime? LastUsedAt { get; set; }
}
