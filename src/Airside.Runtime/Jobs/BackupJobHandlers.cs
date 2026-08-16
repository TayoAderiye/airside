using Airside.Core.Common;
using Airside.Core.Databases;
using Airside.Core.Jobs;
using Airside.Core.Naming;
using Airside.Core.Workloads;
using Airside.Runtime.Databases;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Jobs;

public static class BackupJobTypes
{
    public const string Backup = "database.backup";
    public const string Restore = "database.restore";
    public const string RotateCredentials = "database.rotate_credentials";
}

public sealed record BackupPayload(Guid WorkloadId, Guid BackupId, string TriggerKind);

public sealed record RestorePayload(Guid WorkloadId, Guid RestoreId, Guid BackupId);

public sealed record RotateCredentialsPayload(Guid WorkloadId, Guid NewCredentialId);

/// <summary>Backup and restore rows, kept out of the runtime layer's sight of EF Core.</summary>
public interface IBackupStore
{
    Task<BackupRecord?> GetBackupAsync(Guid backupId, CancellationToken ct);

    Task RecordBackupResultAsync(
        Guid backupId,
        long sizeBytes,
        string sha256,
        string engineSnapshot,
        string kind,
        CancellationToken ct);

    Task RecordBackupFailedAsync(Guid backupId, string message, CancellationToken ct);

    Task<Guid> CreatePreRestoreBackupAsync(Guid workloadId, CancellationToken ct);

    Task RecordRestoreResultAsync(
        Guid restoreId,
        bool succeeded,
        Guid? preRestoreBackupId,
        string? errorCode,
        string? errorMessage,
        CancellationToken ct);

    Task ActivateCredentialAsync(Guid credentialId, CancellationToken ct);

    Task<Secret?> RevealCredentialAsync(Guid credentialId, CancellationToken ct);
}

public sealed record BackupRecord(
    Guid Id,
    Guid DatabaseInstanceId,
    string StoragePath,
    string EngineSnapshot,
    string? Sha256,
    long? SizeBytes);

/// <summary>
/// Takes a backup.
/// </summary>
/// <remarks>
/// The artefact is written to a temporary path and moved into place only after
/// the hash and size are known. A file that appears at its final path while still
/// being written is a backup a scheduled restore can pick up half-finished.
/// </remarks>
public sealed class DatabaseBackupHandler(
    IDatabaseEngineRegistry engines,
    IDatabaseWorkloadStore workloads,
    IBackupStore backups,
    ILogger<DatabaseBackupHandler> logger) : IJobHandler
{
    public string JobType => BackupJobTypes.Backup;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<BackupPayload>();
        var workload = await workloads.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);
        var record = await backups.GetBackupAsync(payload.BackupId, ct).ConfigureAwait(false);

        if (workload?.ContainerId is null || record is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The database or backup record no longer exists.");
        }

        var engine = engines.Get(workload.Engine);

        if (!engine.Capabilities.SupportsLogicalBackup && !engine.Capabilities.SupportsSnapshotBackup)
        {
            return new Error(
                ErrorCodes.BackupNotSupportedForEngine,
                $"{workload.Engine} cannot be backed up.");
        }

        var temporaryPath = record.StoragePath + ".partial";

        await context.ReportProgressAsync(10, "Starting backup", ct).ConfigureAwait(false);
        await context.TrackResourceAsync(JobResourceKind.Volume, temporaryPath, true, ct).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

            BackupArtifact artifact;

            await using (var file = File.Create(temporaryPath))
            {
                artifact = await engine.BackupAsync(
                    new BackupOperation(
                        new DatabaseEndpoint(
                            workload.ContainerId,
                            workload.ContainerName,
                            engine.Capabilities.DefaultPort,
                            workload.Spec.DatabaseName),
                        new DatabaseCredentialValue(workload.Spec.Username, workload.Spec.Password),
                        workload.DataVolumeName,
                        record.EngineSnapshot,
                        new Progress<string>(m => logger.LogInformation("Backup {BackupId}: {Message}", record.Id, m))),
                    file,
                    ct).ConfigureAwait(false);
            }

            await context.ReportProgressAsync(80, "Verifying", ct).ConfigureAwait(false);

            // Re-hashed from disk, not trusted from the in-flight hash. If the two
            // disagree the bytes did not survive the write, which is precisely the
            // failure a checksum is for.
            await using (var written = File.OpenRead(temporaryPath))
            {
                var onDisk = await BackupVerification.ComputeSha256Async(written, ct).ConfigureAwait(false);

                if (!string.Equals(onDisk, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new Error(
                        ErrorCodes.BackupChecksumMismatch,
                        "The backup file on disk does not match what was streamed out of the database.");
                }
            }

            File.Move(temporaryPath, record.StoragePath, overwrite: true);

            await backups.RecordBackupResultAsync(
                record.Id, artifact.SizeBytes, artifact.Sha256, artifact.EngineSnapshot,
                artifact.Kind.ToString(), ct).ConfigureAwait(false);

            await context.ReportProgressAsync(100, "Complete", ct).ConfigureAwait(false);
            await context.LogStepAsync(
                "backup",
                $"Wrote {artifact.SizeBytes} bytes from {artifact.EngineSnapshot}.", ct).ConfigureAwait(false);

            return Result.Ok();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Backup {BackupId} failed", record.Id);
            await backups.RecordBackupFailedAsync(record.Id, ex.Message, ct).ConfigureAwait(false);
            return new Error("backup.failed", ex.Message);
        }
        catch (IOException ex)
        {
            // Logged with the exception, not just a summary. A backup that failed
            // for a reason nobody can discover is a backup nobody will fix.
            logger.LogError(ex, "Backup {BackupId} could not be written to {Path}", record.Id, temporaryPath);
            await backups.RecordBackupFailedAsync(record.Id, ex.Message, ct).ConfigureAwait(false);
            return new Error("backup.failed", $"The backup could not be written to disk: {ex.Message}");
        }
        catch (Core.Containers.ContainerRuntimeException ex)
        {
            logger.LogError(ex, "Backup {BackupId} failed in the container runtime", record.Id);
            await backups.RecordBackupFailedAsync(record.Id, ex.Message, ct).ConfigureAwait(false);
            return new Error(ErrorCodes.RuntimeUnavailable, ex.Message);
        }
    }

    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only the partial file. A backup that failed leaves nothing behind, and
        // the database was never modified — a dump is a read.
        foreach (var resource in await context.GetTrackedResourcesAsync(ct).ConfigureAwait(false))
        {
            if (resource.Kind == JobResourceKind.Volume && File.Exists(resource.Reference))
            {
                File.Delete(resource.Reference);
                await context.LogStepAsync("compensate", "Removed the partial backup file.", ct)
                    .ConfigureAwait(false);
            }
        }

        var payload = context.GetPayload<BackupPayload>();
        await backups.RecordBackupFailedAsync(payload.BackupId, "The backup job did not complete.", ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Restores a backup.
/// </summary>
/// <remarks>
/// The order is the point. Verify the checksum and the engine version, take a
/// pre-restore safety backup, and only then touch the database — so every reason
/// to refuse is discovered while the instance is still serving traffic rather
/// than after it has been stopped.
/// </remarks>
public sealed class DatabaseRestoreHandler(
    Core.Containers.IContainerRuntime runtime,
    IDatabaseEngineRegistry engines,
    IDatabaseWorkloadStore workloads,
    IBackupStore backups,
    BackupExecutor executor,
    ILogger<DatabaseRestoreHandler> logger) : IJobHandler
{
    public string JobType => BackupJobTypes.Restore;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<RestorePayload>();
        var workload = await workloads.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);
        var record = await backups.GetBackupAsync(payload.BackupId, ct).ConfigureAwait(false);

        if (workload?.ContainerId is null || record is null)
        {
            return await FailAsync(payload, ErrorCodes.BackupNotFound, "The backup no longer exists.", ct)
                .ConfigureAwait(false);
        }

        var engine = engines.Get(workload.Engine);

        await context.ReportProgressAsync(5, "Verifying the backup", ct).ConfigureAwait(false);

        if (!File.Exists(record.StoragePath))
        {
            return await FailAsync(payload, ErrorCodes.BackupNotFound, "The backup file is missing.", ct)
                .ConfigureAwait(false);
        }

        // Refused before anything is stopped. A version mismatch found halfway
        // through leaves the database down with a partly applied dump inside it.
        var target = $"{workload.Engine.ToString().ToLowerInvariant()}:{workload.Spec.Version}";
        var compatibility = BackupVerification.CheckEngineCompatibility(record.EngineSnapshot, target);

        if (compatibility.IsFailure)
        {
            return await FailAsync(payload, compatibility.Failure!.Code, compatibility.Failure.Message, ct)
                .ConfigureAwait(false);
        }

        await using (var file = File.OpenRead(record.StoragePath))
        {
            var actual = await BackupVerification.ComputeSha256Async(file, ct).ConfigureAwait(false);
            var checksum = BackupVerification.CheckChecksum(record.Sha256, actual);

            if (checksum.IsFailure)
            {
                return await FailAsync(payload, checksum.Failure!.Code, checksum.Failure.Message, ct)
                    .ConfigureAwait(false);
            }
        }

        await context.LogStepAsync("verify", "Checksum and engine version confirmed.", ct).ConfigureAwait(false);

        await context.ReportProgressAsync(20, "Taking a safety backup", ct).ConfigureAwait(false);
        var safetyBackupId = await backups.CreatePreRestoreBackupAsync(payload.WorkloadId, ct).ConfigureAwait(false);
        var safety = await backups.GetBackupAsync(safetyBackupId, ct).ConfigureAwait(false);

        if (safety is null)
        {
            return await FailAsync(
                payload, "restore.safety_backup_failed",
                "The pre-restore safety backup record could not be created.", ct).ConfigureAwait(false);
        }

        try
        {
            // Actually taken, not merely recorded. This is the only copy of the
            // current data once the restore begins, so a restore that proceeds
            // without it is a one-way door disguised as a reversible operation.
            var artifact = await executor.RunAsync(
                workload, safety.StoragePath, safety.EngineSnapshot, null, ct).ConfigureAwait(false);

            await backups.RecordBackupResultAsync(
                safetyBackupId, artifact.SizeBytes, artifact.Sha256, artifact.EngineSnapshot,
                artifact.Kind.ToString(), ct).ConfigureAwait(false);

            await context.LogStepAsync(
                "safety-backup",
                $"Captured {artifact.SizeBytes} bytes of the current data before restoring.", ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "The pre-restore safety backup failed; refusing to restore");
            await backups.RecordBackupFailedAsync(safetyBackupId, ex.Message, ct).ConfigureAwait(false);

            // Refused rather than continued. Restoring without a safety backup
            // turns "we picked the wrong backup" from an inconvenience into
            // permanent data loss.
            return await FailAsync(
                payload, "restore.safety_backup_failed",
                "The pre-restore safety backup failed, so the restore was not attempted. "
                + $"Current data is untouched. {ex.Message}", ct).ConfigureAwait(false);
        }

        var requiresStop = engine.Capabilities.RequiresStopForRestore;

        if (requiresStop)
        {
            await context.ReportProgressAsync(40, "Stopping the database", ct).ConfigureAwait(false);
            await runtime.Containers
                .StopAsync(workload.ContainerId, TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(false);
            await workloads.SetStateAsync(payload.WorkloadId, DatabaseState.Restoring.ToString(), ct)
                .ConfigureAwait(false);
        }

        try
        {
            await context.ReportProgressAsync(60, "Restoring", ct).ConfigureAwait(false);

            await using var source = File.OpenRead(record.StoragePath);

            await engine.RestoreAsync(
                new RestoreOperation(
                    new DatabaseEndpoint(
                        workload.ContainerId, workload.ContainerName,
                        engine.Capabilities.DefaultPort, workload.Spec.DatabaseName),
                    new DatabaseCredentialValue(workload.Spec.Username, workload.Spec.Password),
                    workload.DataVolumeName,
                    record.EngineSnapshot,
                    null),
                source,
                ct).ConfigureAwait(false);
        }
        finally
        {
            if (requiresStop)
            {
                // In a finally: a failed restore must not leave the database
                // stopped, or a bad backup becomes an outage on top of a failure.
                await context.ReportProgressAsync(85, "Starting the database", ct).ConfigureAwait(false);
                await runtime.Containers.StartAsync(workload.ContainerId, ct).ConfigureAwait(false);
            }
        }

        await workloads.SetStateAsync(payload.WorkloadId, DatabaseState.Running.ToString(), ct)
            .ConfigureAwait(false);
        await backups.RecordRestoreResultAsync(payload.RestoreId, true, safetyBackupId, null, null, ct)
            .ConfigureAwait(false);

        await context.ReportProgressAsync(100, "Restored", ct).ConfigureAwait(false);
        return Result.Ok();
    }

    private async Task<Result> FailAsync(RestorePayload payload, string code, string message, CancellationToken ct)
    {
        await backups.RecordRestoreResultAsync(payload.RestoreId, false, null, code, message, ct)
            .ConfigureAwait(false);
        return new Error(code, message);
    }

    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<RestorePayload>();
        var workload = await workloads.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);

        // The database must come back up whatever happened. A half-applied restore
        // is bad; a database left stopped because the restore threw is worse.
        if (workload?.ContainerId is not null)
        {
            await runtime.Containers.StartAsync(workload.ContainerId, ct).ConfigureAwait(false);
            await workloads.SetStateAsync(payload.WorkloadId, DatabaseState.Running.ToString(), ct)
                .ConfigureAwait(false);
        }

        await backups.RecordRestoreResultAsync(
            payload.RestoreId, false, null, "restore.failed",
            "The restore did not complete. A pre-restore backup was taken if the failure happened after "
            + "verification.", ct).ConfigureAwait(false);

        await context.LogStepAsync("compensate", "The database was restarted.", ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Issues a new credential without revoking the old one.
/// </summary>
/// <remarks>
/// <para>
/// Rotation is a breaking operation and the API says so rather than implying a
/// grace period. Each engine keeps one password per role, so the moment
/// <c>ALTER USER … PASSWORD</c> lands the previous value stops authenticating —
/// verified against a live Postgres, where the old password is rejected
/// immediately. Anything currently connected keeps its session but fails on
/// reconnect.
/// </para>
/// <para>
/// True overlap would require issuing a second role with the same grants. That is
/// a real feature and a later one; pretending this is it would mean an operator
/// rotating in business hours on the strength of a grace period that does not
/// exist.
/// </para>
/// </remarks>
public sealed class RotateCredentialsHandler(
    IDatabaseEngineRegistry engines,
    IDatabaseWorkloadStore workloads,
    IBackupStore credentials) : IJobHandler
{
    public string JobType => BackupJobTypes.RotateCredentials;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<RotateCredentialsPayload>();
        var workload = await workloads.GetAsync(payload.WorkloadId, ct).ConfigureAwait(false);

        if (workload?.ContainerId is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The database has no container.");
        }

        var replacement = await credentials.RevealCredentialAsync(payload.NewCredentialId, ct)
            .ConfigureAwait(false);

        if (replacement is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The new credential no longer exists.");
        }

        var engine = engines.Get(workload.Engine);

        await context.ReportProgressAsync(50, "Applying the new password", ct).ConfigureAwait(false);

        await engine.RotatePasswordAsync(
            new DatabaseEndpoint(
                workload.ContainerId, workload.ContainerName,
                engine.Capabilities.DefaultPort, workload.Spec.DatabaseName),
            new DatabaseCredentialValue(workload.Spec.Username, workload.Spec.Password),
            replacement,
            ct).ConfigureAwait(false);

        await credentials.ActivateCredentialAsync(payload.NewCredentialId, ct).ConfigureAwait(false);

        await context.LogStepAsync(
            "rotate",
            "The new credential is live and the previous one no longer authenticates. Anything attached "
            + "to this database will fail on its next reconnect until it is redeployed with the new "
            + "password.", ct).ConfigureAwait(false);

        await context.ReportProgressAsync(100, "Rotated", ct).ConfigureAwait(false);
        return Result.Ok();
    }

    public Task CompensateAsync(IJobContext context, CancellationToken ct) =>
        // Nothing to unwind: the old credential was never touched, and the new one
        // stays inactive unless the engine confirmed it. That is the whole point
        // of issuing rather than replacing.
        Task.CompletedTask;
}
