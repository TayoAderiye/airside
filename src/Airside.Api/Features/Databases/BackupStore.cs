using Airside.Core.Common;
using Airside.Core.Naming;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Databases;

internal sealed class BackupStore(
    AirsideDbContext db,
    ISecretProtector protector,
    TimeProvider timeProvider) : IBackupStore
{
    public async Task<BackupRecord?> GetBackupAsync(Guid backupId, CancellationToken ct)
    {
        var backup = await db.Backups
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == backupId, ct)
            .ConfigureAwait(false);

        return backup is null
            ? null
            : new BackupRecord(
                backup.Id, backup.DatabaseInstanceId, backup.StoragePath,
                backup.EngineSnapshot, backup.Sha256, backup.SizeBytes);
    }

    public async Task RecordBackupResultAsync(
        Guid backupId,
        long sizeBytes,
        string sha256,
        string engineSnapshot,
        string kind,
        CancellationToken ct)
    {
        var backup = await db.Backups.FirstOrDefaultAsync(b => b.Id == backupId, ct).ConfigureAwait(false);

        if (backup is null)
        {
            return;
        }

        var database = await db.Databases
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == backup.DatabaseInstanceId, ct)
            .ConfigureAwait(false);

        backup.SizeBytes = sizeBytes;
        backup.Sha256 = sha256;
        backup.EngineSnapshot = engineSnapshot;
        backup.Status = BackupStatus.Succeeded;
        backup.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;

        // Retention is stamped now rather than computed at prune time, so
        // changing the policy later cannot retroactively expire backups somebody
        // is relying on.
        if (database?.BackupRetentionDays is { } days and > 0)
        {
            backup.ExpiresAt = backup.CompletedAt.Value.AddDays(days);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordBackupFailedAsync(Guid backupId, string message, CancellationToken ct)
    {
        var backup = await db.Backups.FirstOrDefaultAsync(b => b.Id == backupId, ct).ConfigureAwait(false);

        if (backup is null || backup.Status == BackupStatus.Succeeded)
        {
            return;
        }

        backup.Status = BackupStatus.Failed;
        backup.ErrorMessage = message.Length > 1024 ? message[..1024] : message;
        backup.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Guid> CreatePreRestoreBackupAsync(Guid workloadId, CancellationToken ct)
    {
        var database = await db.Databases
            .AsNoTracking()
            .FirstAsync(d => d.Id == workloadId, ct)
            .ConfigureAwait(false);

        var backup = new Backup
        {
            DatabaseInstanceId = workloadId,
            Kind = Core.Databases.BackupKind.Logical,
            TriggerKind = BackupTriggerKind.PreRestore,
            Status = BackupStatus.Running,
            StoragePath = BackupPath(database),
            EngineSnapshot = $"{database.Engine.ToString().ToLowerInvariant()}:{database.Version}",
            DatabaseNameSnapshot = database.DatabaseName,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,

            // Pinned. The whole reason this exists is to be there when someone
            // says "we restored the wrong backup", and retention must not quietly
            // remove it first.
            IsRetained = true,
        };

        db.Backups.Add(backup);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return backup.Id;
    }

    public async Task RecordRestoreResultAsync(
        Guid restoreId,
        bool succeeded,
        Guid? preRestoreBackupId,
        string? errorCode,
        string? errorMessage,
        CancellationToken ct)
    {
        var restore = await db.Restores.FirstOrDefaultAsync(r => r.Id == restoreId, ct).ConfigureAwait(false);

        if (restore is null)
        {
            return;
        }

        restore.Status = succeeded ? RestoreStatus.Succeeded : RestoreStatus.Failed;
        restore.PreRestoreBackupId = preRestoreBackupId ?? restore.PreRestoreBackupId;
        restore.ErrorCode = errorCode;
        restore.ErrorMessage = errorMessage;
        restore.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ActivateCredentialAsync(Guid credentialId, CancellationToken ct)
    {
        var credential = await db.DatabaseCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct)
            .ConfigureAwait(false);

        if (credential is null)
        {
            return;
        }

        var previous = await db.DatabaseCredentials
            .Where(c => c.DatabaseInstanceId == credential.DatabaseInstanceId
                && c.Id != credentialId
                && c.State == CredentialState.Active)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Superseded, not revoked. The old credential keeps working until an
        // operator explicitly revokes it, which is what gives attached
        // applications a window to be redeployed.
        foreach (var old in previous)
        {
            old.State = CredentialState.Superseded;
            old.SupersededAt = timeProvider.GetUtcNow().UtcDateTime;
            old.IsPrimary = false;
        }

        credential.State = CredentialState.Active;
        credential.IsPrimary = true;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Secret?> RevealCredentialAsync(Guid credentialId, CancellationToken ct)
    {
        var credential = await db.DatabaseCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct)
            .ConfigureAwait(false);

        if (credential is null)
        {
            return null;
        }

        var revealed = protector.Unprotect(credential.EncryptedPassword);
        return revealed.IsSuccess ? revealed.Value : null;
    }

    public static string BackupPath(DatabaseInstance database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var extension = database.Engine == Core.Databases.DatabaseEngineKind.Redis ? "rdb" : "dump";

        // Under the managed backup root, with a name derived from the validated
        // slug and a v7 id. Nothing here comes from a request body.
        return Path.Combine(
            AirsideLabels.HostPaths.Backups,
            database.Slug,
            $"{Guid.CreateVersion7():N}.{extension}");
    }
}
