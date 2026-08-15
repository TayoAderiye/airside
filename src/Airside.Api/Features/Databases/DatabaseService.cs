using Airside.Api.Contracts;
using Airside.Api.Hosting;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Hosting;
using Airside.Core.Jobs;
using Airside.Core.Naming;
using Airside.Core.Security;
using Airside.Core.Workloads;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Databases;
using Airside.Runtime.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Databases;

/// <summary>
/// Serialises admission across the whole process.
/// </summary>
/// <remarks>
/// Two simultaneous provision requests can both pass a naive capacity check and
/// together exceed the host. Airside is a single instance managing a single host,
/// so an in-process semaphore around check-and-reserve is both sufficient and the
/// simplest thing that is correct. The reservation is written in the same
/// transaction as the workload row, so the multi-host future replaces this with a
/// <c>SELECT … FOR UPDATE</c> on the host row without restructuring the callers —
/// and that future needs Postgres, since SQLite has no row locks.
/// </remarks>
public sealed class AllocationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<T> WithExclusiveAccessAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class DatabaseService(
    AirsideDbContext db,
    IDatabaseEngineRegistry engines,
    IAllocationPolicy allocationPolicy,
    IHostAllocationReader allocationReader,
    AllocationGate gate,
    ISecretProtector protector,
    ISecretGenerator generator,
    IJobQueue jobs,
    TimeProvider timeProvider)
{
    public async Task<Result<JobAccepted>> ProvisionAsync(
        CreateDatabaseRequest request,
        Guid? userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var slugResult = Slug.Create(request.Slug);

        if (slugResult.IsFailure)
        {
            return slugResult.Failure!;
        }

        var slug = slugResult.Value;

        if (!Enum.TryParse<DatabaseEngineKind>(request.Engine, ignoreCase: true, out var engineKind))
        {
            return new Error(
                ErrorCodes.DatabaseEngineUnsupported,
                $"'{request.Engine}' is not a supported engine.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["supported"] = engines.All.Select(e => e.Kind.ToString().ToLowerInvariant()).ToList(),
                });
        }

        var engine = engines.Get(engineKind);
        var password = request.Password is null ? generator.GeneratePassword() : new Secret(request.Password);
        var workloadId = Guid.CreateVersion7();

        var spec = new DatabaseProvisionSpec
        {
            WorkloadId = workloadId,
            Slug = slug,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? slug.Value : request.DisplayName,
            Engine = engineKind,
            Version = request.Version,
            CpuNanos = request.CpuNanos,
            MemoryBytes = request.MemoryBytes,
            StorageBytes = request.StorageBytes,
            AutoRestart = request.AutoRestart,
            PublishedPort = request.PublishedPort,
            PublishBindAddress = request.PublishBindAddress ?? PortBinding.Loopback,
            DatabaseName = request.DatabaseName,
            Username = request.Username,
            Password = password,
            MaxMemoryBytes = request.MaxMemoryBytes,
            MaxMemoryPolicy = request.MaxMemoryPolicy,
            AofEnabled = request.AofEnabled,
            BackupEnabled = request.BackupEnabled,
        };

        var validation = engine.Validate(spec);

        if (validation.IsFailure)
        {
            return validation.Failure!;
        }

        if (request.PublishBindAddress is not null
            && request.PublishBindAddress != PortBinding.Loopback
            && request.PublishBindAddress != PortBinding.AllInterfaces)
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "A published database may bind to 127.0.0.1 or 0.0.0.0 only.");
        }

        return await gate.WithExclusiveAccessAsync(async () =>
        {
            if (await db.Workloads.AnyAsync(w => w.Slug == slug.Value, ct).ConfigureAwait(false))
            {
                return (Result<JobAccepted>)new Error(
                    ErrorCodes.WorkloadSlugTaken,
                    $"A workload named '{slug.Value}' already exists.");
            }

            var position = await allocationReader.ReadPositionAsync(ct).ConfigureAwait(false);
            var admission = allocationPolicy.Admit(
                position,
                new ResourceTriple(spec.CpuNanos, spec.MemoryBytes, spec.StorageBytes));

            if (admission.IsFailure)
            {
                return (Result<JobAccepted>)admission.Failure!;
            }

            var host = await db.Hosts.FirstAsync(ct).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow().UtcDateTime;

            var database = new DatabaseInstance
            {
                Id = workloadId,
                HostId = host.Id,
                Kind = WorkloadKind.Database,
                Slug = slug.Value,
                DisplayName = spec.DisplayName,
                State = DatabaseState.Provisioning.ToString(),
                StateChangedAt = now,
                CpuLimitNanos = spec.CpuNanos,
                MemoryLimitBytes = spec.MemoryBytes,
                StorageAllocationBytes = spec.StorageBytes,
                AutoRestart = spec.AutoRestart,
                NetworkName = AirsideNames.DatabaseNetwork(slug),
                CreatedByUserId = userId,
                Engine = engineKind,
                Version = spec.Version,
                ImageRef = engine.ResolveImage(spec.Version).ToString(),
                DatabaseName = spec.DatabaseName,
                PublishedPort = spec.PublishedPort,
                PublishBindAddress = spec.PublishBindAddress,
                MaxMemoryBytes = spec.MaxMemoryBytes,
                MaxMemoryPolicy = spec.MaxMemoryPolicy,
                AofEnabled = spec.AofEnabled,
                BackupEnabled = spec.BackupEnabled,
                BackupCron = request.BackupCron,
                BackupRetentionCount = request.BackupRetentionCount,
                BackupRetentionDays = request.BackupRetentionDays,
            };

            database.Credentials.Add(new DatabaseCredential
            {
                DatabaseInstanceId = workloadId,
                Username = spec.Username,
                EncryptedPassword = protector.Protect(password),
                IsPrimary = true,
                State = CredentialState.Active,
            });

            db.Databases.Add(database);

            // The volume row is written now, before the container exists, so the
            // storage it will occupy counts against capacity from this moment
            // rather than from whenever the job happens to run.
            db.Volumes.Add(new Volume
            {
                HostId = host.Id,
                WorkloadId = workloadId,
                Name = AirsideNames.Volume(slug, "data"),
                MountPath = "/data",
                Purpose = VolumePurpose.Data,
                SizeAllocationBytes = spec.StorageBytes,
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var jobId = await jobs.EnqueueAsync(
                DatabaseJobTypes.Provision,
                new DatabaseProvisionPayload(workloadId),
                workloadId,
                userId,
                $"{DatabaseJobTypes.Provision}:{workloadId}",
                ct).ConfigureAwait(false);

            return (Result<JobAccepted>)JobAccepted.From(jobId, DatabaseJobTypes.Provision, workloadId);
        }, ct).ConfigureAwait(false);
    }

    public async Task<Result<JobAccepted>> LifecycleAsync(
        Guid id,
        string jobType,
        DatabaseState transitionalState,
        Guid? userId,
        CancellationToken ct)
    {
        var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);

        if (database is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such database.");
        }

        var transition = WorkloadTransitions.Check(database.CurrentState, transitionalState);

        if (transition.IsFailure)
        {
            return transition.Failure!;
        }

        var jobId = await jobs.EnqueueAsync(
            jobType,
            new DatabaseLifecyclePayload(id),
            id,
            userId,
            // Time-qualified: unlike a provision, restarting twice is a legitimate
            // thing to want, so the key must not collapse the second request into
            // the first.
            $"{jobType}:{id}:{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}",
            ct).ConfigureAwait(false);

        return JobAccepted.From(jobId, jobType, id);
    }

    public async Task<Result<JobAccepted>> DeleteAsync(
        Guid id,
        DeleteDatabaseRequest request,
        Guid? userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);

        if (database is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such database.");
        }

        // Typed confirmation of the resource's own name. The comparison is ordinal
        // and exact — a "close enough" match defeats the point of asking.
        if (!string.Equals(request.ConfirmSlug, database.Slug, StringComparison.Ordinal))
        {
            return new Error(
                ErrorCodes.WorkloadConfirmationMismatch,
                "Type the database's name exactly to confirm deletion.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["expected"] = database.Slug });
        }

        var jobId = await jobs.EnqueueAsync(
            DatabaseJobTypes.Delete,
            new DatabaseDeletePayload(id, request.DeleteVolume),
            id,
            userId,
            $"{DatabaseJobTypes.Delete}:{id}",
            ct).ConfigureAwait(false);

        return JobAccepted.From(jobId, DatabaseJobTypes.Delete, id);
    }

    public async Task<Result<JobAccepted>> ResizeAsync(
        Guid id,
        ResizeDatabaseRequest request,
        Guid? userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await gate.WithExclusiveAccessAsync(async () =>
        {
            var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);

            if (database is null)
            {
                return (Result<JobAccepted>)new Error(ErrorCodes.WorkloadNotFound, "No such database.");
            }

            var position = await allocationReader.ReadPositionAsync(ct).ConfigureAwait(false);

            // Only the increase is admitted. Charging the full new size against a
            // host that is already carrying the old one would reject every resize
            // on a busy box, including shrinks.
            var delta = new ResourceTriple(
                Math.Max(0, request.CpuNanos - database.CpuLimitNanos),
                Math.Max(0, request.MemoryBytes - database.MemoryLimitBytes),
                Math.Max(0, request.StorageBytes - database.StorageAllocationBytes));

            var admission = allocationPolicy.Admit(position, delta);

            if (admission.IsFailure)
            {
                return (Result<JobAccepted>)admission.Failure!;
            }

            var jobId = await jobs.EnqueueAsync(
                DatabaseJobTypes.Resize,
                new DatabaseResizePayload(id, request.CpuNanos, request.MemoryBytes, request.StorageBytes),
                id,
                userId,
                $"{DatabaseJobTypes.Resize}:{id}:{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}",
                ct).ConfigureAwait(false);

            return (Result<JobAccepted>)JobAccepted.From(jobId, DatabaseJobTypes.Resize, id);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Warnings that are true of a configuration but do not stop it being accepted.</summary>
    public static IReadOnlyList<WarningDto> WarningsFor(DatabaseInstance database, IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(engine);

        var warnings = new List<WarningDto>();

        if (string.Equals(database.MaxMemoryPolicy, "noeviction", StringComparison.Ordinal))
        {
            warnings.Add(new WarningDto(
                "redis.noeviction_write_failure_risk",
                "maxmemory-policy is noeviction. When this instance reaches maxmemory it will start "
                + "rejecting writes rather than evicting keys, and applications will see errors."));
        }

        if (engine.Capabilities.RequiresMaxMemory
            && database.MaxMemoryBytes is { } max
            && database.MemoryLimitBytes > 0
            && (double)max / database.MemoryLimitBytes > 0.70
            && (database.AofEnabled == true || database.BackupEnabled))
        {
            warnings.Add(new WarningDto(
                "redis.maxmemory_headroom_low",
                "maxmemory is above 70% of the container limit while persistence is enabled. Redis forks "
                + "during BGSAVE and AOF rewrite, and copy-on-write can push the container into the "
                + "kernel's OOM killer mid-backup.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["maxMemoryBytes"] = max,
                    ["memoryLimitBytes"] = database.MemoryLimitBytes,
                    ["fraction"] = Math.Round((double)max / database.MemoryLimitBytes, 3),
                }));
        }

        if (string.Equals(database.PublishBindAddress, PortBinding.AllInterfaces, StringComparison.Ordinal))
        {
            warnings.Add(new WarningDto(
                "database.published_publicly",
                "This database is published on 0.0.0.0 and is reachable from the internet if the host's "
                + "firewall allows it. Anyone who obtains the password can connect directly."));
        }

        return warnings;
    }
}
