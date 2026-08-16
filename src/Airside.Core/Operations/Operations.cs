namespace Airside.Core.Operations;

/// <summary>
/// Raises operator-facing notifications, collapsing repeats of one condition.
/// </summary>
/// <remarks>
/// Dedupe is the whole point, not an optimisation. The conditions worth notifying
/// about are noticed by sweeps that run on timers, so every one of them is
/// observed again and again for as long as it holds. A certificate expiring in a
/// week is one fact; appending it four times a day produces a list nobody reads,
/// and the notification that mattered is lost in it.
/// </remarks>
public interface INotifier
{
    /// <summary>Raises or refreshes a notification, keyed by <see cref="NotificationRequest.DedupeKey"/>.</summary>
    Task RaiseAsync(NotificationRequest notification, CancellationToken ct);

    /// <summary>
    /// Marks a condition as no longer true.
    /// </summary>
    /// <remarks>
    /// Resolving rather than deleting, so that looking back distinguishes
    /// "this broke and recovered" from "this never happened".
    /// </remarks>
    Task ResolveAsync(string dedupeKey, CancellationToken ct);
}

/// <param name="DedupeKey">
/// Identifies the condition, not the occurrence — <c>certificate.expiring:app.example.com</c>,
/// not a new value each time. Two raises with the same key are one fact seen twice.
/// </param>
public sealed record NotificationRequest(
    string DedupeKey,
    NotificationLevel Level,
    string Title,
    string Body,
    string? Code = null,
    string? ResourceKind = null,
    Guid? ResourceId = null);

public enum NotificationLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Backs up the control plane's own store and key ring.
/// </summary>
/// <remarks>
/// Distinct from workload backups because losing this is worse: the Data
/// Protection key ring is what decrypts every stored credential, so a database
/// restored without it is a list of secrets nobody can read. Both go in one
/// archive so they cannot be separated by accident.
/// </remarks>
public interface ISystemBackupProvider
{
    Task<SystemBackupResult> CreateAsync(string destinationDirectory, CancellationToken ct);

    /// <summary>Checks an archive before anything is overwritten with it.</summary>
    Task<SystemBackupVerification> VerifyAsync(string archivePath, CancellationToken ct);
}

public sealed record SystemBackupResult(
    string ArchivePath,
    long SizeBytes,
    string Sha256,
    string StoreProvider,
    DateTimeOffset CreatedAt);

/// <param name="KeyRingIncluded">
/// False means every secret in the backup is undecryptable. Worth refusing a
/// restore over rather than discovering afterwards.
/// </param>
public sealed record SystemBackupVerification(
    bool IsUsable,
    string? StoreProvider,
    bool KeyRingIncluded,
    string? Detail);

/// <summary>
/// Time-based one-time passwords, for the second factor.
/// </summary>
/// <remarks>
/// Implemented rather than taken from a package: RFC 6238 is a HMAC and a
/// truncation, the surface is tiny, and a dependency here would be one more thing
/// with access to the authentication path.
/// </remarks>
public interface ITotp
{
    string GenerateSecret();

    /// <summary>The <c>otpauth://</c> URI an authenticator app scans.</summary>
    string BuildProvisioningUri(string secret, string issuer, string account);

    /// <summary>
    /// Verifies a code, returning the time step it matched.
    /// </summary>
    /// <remarks>
    /// The step is returned so the caller can refuse a replay. A TOTP code stays
    /// valid for its whole window, so one captured in transit works again until
    /// the window closes unless the last accepted step is remembered.
    /// </remarks>
    bool TryValidate(string secret, string code, long notBeforeStep, out long matchedStep);
}
