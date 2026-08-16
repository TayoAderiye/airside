namespace Airside.Core.Common;

/// <summary>
/// Every error code Airside returns. These are an API contract — the UI switches
/// on them, so renaming one is a breaking change (CONVENTIONS.md §3).
/// </summary>
public static class ErrorCodes
{
    // Validation
    public const string ValidationFailed = "validation.failed";
    public const string ValidationInvalidSlug = "validation.invalid_slug";
    public const string ValidationUnsupportedVersion = "validation.unsupported_version";
    public const string ValidationFieldNotApplicable = "validation.field_not_applicable";
    public const string ValidationFieldRequired = "validation.field_required";

    // Resource allocation
    public const string ResourceInsufficientMemory = "resource.insufficient_memory";
    public const string ResourceInsufficientCpu = "resource.insufficient_cpu";
    public const string ResourceInsufficientStorage = "resource.insufficient_storage";

    // Workloads
    public const string WorkloadNotFound = "workload.not_found";
    public const string WorkloadSlugTaken = "workload.slug_taken";
    public const string WorkloadInvalidTransition = "workload.invalid_transition";
    public const string WorkloadBusy = "workload.busy";
    public const string WorkloadSystemProtected = "workload.system_protected";
    public const string WorkloadConfirmationMismatch = "workload.confirmation_mismatch";

    // Databases
    public const string DatabaseEngineUnsupported = "database.engine_unsupported";
    public const string DatabaseAttachmentExists = "database.attachment_exists";
    public const string DatabaseEnvPrefixConflict = "database.env_prefix_conflict";

    // Backup and restore
    public const string BackupNotFound = "backup.not_found";
    public const string BackupChecksumMismatch = "backup.checksum_mismatch";
    public const string BackupEngineVersionMismatch = "backup.engine_version_mismatch";
    public const string BackupNotSupportedForEngine = "backup.not_supported_for_engine";

    // Query console
    public const string QueryCommandBlocked = "query.command_blocked";
    public const string QueryCommandRequiresElevation = "query.command_requires_elevation";
    public const string QueryTimeout = "query.timeout";

    // Applications and deployment
    public const string ApplicationBuildFailed = "application.build_failed";
    public const string ApplicationHealthCheckFailed = "application.health_check_failed";
    public const string DeploymentNotFound = "deployment.not_found";
    public const string DeploymentImagePruned = "deployment.image_pruned";

    // Networking
    public const string DomainAlreadyBound = "domain.already_bound";
    public const string DomainCertificateFailed = "domain.certificate_failed";

    // Environment and secrets
    public const string EnvironmentKeyConflict = "environment.key_conflict";
    public const string EnvironmentKeyReserved = "environment.key_reserved";

    // Access control
    public const string AuthInvalidCredentials = "auth.invalid_credentials";
    public const string AuthAccountLocked = "auth.account_locked";
    public const string AuthPermissionDenied = "auth.permission_denied";
    public const string AuthLastSuperAdmin = "auth.last_super_admin";
    public const string AuthSetupTokenInvalid = "auth.setup_token_invalid";

    /// <summary>
    /// The password was right and a second factor is enrolled, so the client
    /// should ask for a code and try again.
    /// </summary>
    /// <remarks>
    /// This is deliberately distinct from <see cref="AuthInvalidCredentials"/>,
    /// and it does confirm that the password was correct. That is not an
    /// enumeration leak worth closing: reaching it already requires the
    /// password, and the alternative is a login form that cannot tell the user
    /// why it will not let them in.
    /// </remarks>
    public const string AuthMfaRequired = "auth.mfa_required";

    public const string AuthMfaInvalid = "auth.mfa_invalid";

    // Jobs
    public const string JobNotFound = "job.not_found";
    public const string JobNotCancellable = "job.not_cancellable";

    // Infrastructure
    public const string RuntimeUnavailable = "runtime.unavailable";
    public const string ProxyUnavailable = "proxy.unavailable";
}
