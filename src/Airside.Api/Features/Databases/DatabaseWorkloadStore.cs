using Airside.Core.Common;
using Airside.Core.Databases;
using Airside.Core.Naming;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Databases;

/// <summary>
/// The persistence side of the database job handlers.
/// </summary>
/// <remarks>
/// Lives here rather than in Airside.Runtime so that the runtime layer keeps its
/// rule of never referencing EF Core. The handlers see a flattened snapshot with
/// the password already decrypted; they never touch a DbContext or a cipher.
/// </remarks>
internal sealed class DatabaseWorkloadStore(
    AirsideDbContext db,
    ISecretProtector protector,
    TimeProvider timeProvider) : IDatabaseWorkloadStore
{
    public async Task<DatabaseWorkloadSnapshot?> GetAsync(Guid workloadId, CancellationToken ct)
    {
        var database = await db.Databases
            .Include(d => d.Credentials)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == workloadId, ct)
            .ConfigureAwait(false);

        if (database is null)
        {
            return null;
        }

        var credential = database.Credentials.FirstOrDefault(c => c.IsPrimary && c.State == CredentialState.Active);

        if (credential is null)
        {
            return null;
        }

        var password = protector.Unprotect(credential.EncryptedPassword);

        if (password.IsFailure)
        {
            // A key ring from a different instance. Failing here is better than
            // provisioning a database with a password nobody can reproduce.
            return null;
        }

        if (!Slug.TryCreate(database.Slug, out var slug))
        {
            return null;
        }

        var spec = new DatabaseProvisionSpec
        {
            WorkloadId = database.Id,
            Slug = slug,
            DisplayName = database.DisplayName,
            Engine = database.Engine,
            Version = database.Version,
            CpuNanos = database.CpuLimitNanos,
            MemoryBytes = database.MemoryLimitBytes,
            StorageBytes = database.StorageAllocationBytes,
            AutoRestart = database.AutoRestart,
            PublishedPort = database.PublishedPort,
            PublishBindAddress = database.PublishBindAddress ?? Core.Containers.PortBinding.Loopback,
            DatabaseName = database.DatabaseName,
            Username = credential.Username,
            Password = password.Value,
            MaxMemoryBytes = database.MaxMemoryBytes,
            MaxMemoryPolicy = database.MaxMemoryPolicy,
            AofEnabled = database.AofEnabled,
            BackupEnabled = database.BackupEnabled,
        };

        return new DatabaseWorkloadSnapshot(
            database.Id,
            slug,
            database.DisplayName,
            database.Engine,
            spec,
            database.ContainerId,
            AirsideNames.Volume(slug, "data"),
            AirsideNames.DatabaseNetwork(slug),
            AirsideNames.DatabaseContainer(slug));
    }

    public async Task SetStateAsync(Guid workloadId, string state, CancellationToken ct)
    {
        var workload = await db.Workloads
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workloadId, ct)
            .ConfigureAwait(false);

        if (workload is null)
        {
            return;
        }

        workload.State = state;
        workload.StateChangedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordProvisionedAsync(
        Guid workloadId,
        string containerId,
        string? imageDigest,
        string networkId,
        CancellationToken ct)
    {
        var database = await db.Databases
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == workloadId, ct)
            .ConfigureAwait(false);

        if (database is null)
        {
            return;
        }

        database.ContainerId = containerId;
        database.ImageDigest = imageDigest;
        database.NetworkId = networkId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordLimitsAsync(
        Guid workloadId,
        long cpuNanos,
        long memoryBytes,
        long storageBytes,
        CancellationToken ct)
    {
        var workload = await db.Workloads
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workloadId, ct)
            .ConfigureAwait(false);

        if (workload is null)
        {
            return;
        }

        workload.CpuLimitNanos = cpuNanos;
        workload.MemoryLimitBytes = memoryBytes;
        workload.StorageAllocationBytes = storageBytes;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordDeletedAsync(Guid workloadId, bool volumesRemoved, CancellationToken ct)
    {
        var workload = await db.Workloads
            .Include(w => w.Volumes)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workloadId, ct)
            .ConfigureAwait(false);

        if (workload is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        workload.State = Core.Workloads.DatabaseState.Deleted.ToString();
        workload.StateChangedAt = now;
        workload.DeletedAt = now;
        workload.ContainerId = null;

        foreach (var volume in workload.Volumes)
        {
            if (volumesRemoved)
            {
                volume.DeletedAt = now;
            }
            else
            {
                // Kept, and still counted against allocated storage. The workload
                // row survives soft-deleted so the reclaim screen can say which
                // database this volume came from.
                volume.OrphanedAt = now;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
