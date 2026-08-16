using Airside.Core.Containers;
using Airside.Core.Naming;
using Airside.Core.Operations;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Operations;

public sealed class UpdateOptions
{
    public const string Section = "Airside:Update";

    /// <summary>The repository the control-plane image is pulled from.</summary>
    public string ImageRepository { get; set; } = "ghcr.io/tayo/airside";

    /// <summary>Where <c>state.json</c> lives. A host path, so it survives the container being replaced.</summary>
    public string StatePath { get; set; } = AirsideLabels.HostPaths.State;

    public string BackupRoot { get; set; } = AirsideLabels.HostPaths.Backups;

    /// <summary>How long the replacement gets to report healthy before it is rolled back.</summary>
    public int HealthTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Replaces the control-plane container with a newer one.
/// </summary>
/// <remarks>
/// <para>
/// The awkward part of this is structural: the thing performing the update is the
/// thing being replaced. Once the old container stops, no Airside process is
/// running to notice a failure, retry, or report anything — so every decision has
/// to be recorded on disk <em>before</em> the step that might not survive.
/// </para>
/// <para>
/// The order is: back up, pull, write state, then swap. Backing up first means a
/// failure at any later point still has a restorable snapshot from before
/// anything changed, and pulling before the swap means the window with no control
/// plane does not also contain a network download.
/// </para>
/// <para>
/// Rollback is by digest, never by tag. Re-pulling <c>:0.1.0</c> after a re-push
/// gets a different build than the one that was working, which is not a rollback.
/// </para>
/// </remarks>
public sealed class UpdateOrchestrator(
    IContainerRuntime runtime,
    ISystemBackupProvider backups,
    IServiceScopeFactory scopeFactory,
    UpdateOptions options,
    TimeProvider timeProvider,
    ILogger<UpdateOrchestrator> logger)
{
    /// <summary>
    /// Prepares an update as far as it can be taken from inside the old container.
    /// </summary>
    /// <remarks>
    /// Everything up to but not including the swap. The swap itself cannot be
    /// performed by this process — stopping its own container kills it mid-call —
    /// so it is handed to a detached updater started from the current image, and
    /// this method's job is to leave that updater everything it needs on disk.
    /// </remarks>
    public async Task<Result> PrepareAsync(string targetVersion, Guid? userId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

        var settings = await db.InstanceSettings.FirstAsync(ct).ConfigureAwait(false);
        var current = settings.CurrentImageTag ?? "unknown";

        if (string.Equals(current, targetVersion, StringComparison.Ordinal))
        {
            return Result.AlreadyCurrent;
        }

        var record = new UpdateRecord
        {
            Id = Guid.CreateVersion7(),
            FromVersion = current,
            ToVersion = targetVersion,
            Status = UpdateStatus.Pending,
            StartedByUserId = userId,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
        };

        db.UpdateRecords.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var api = await runtime.Containers
            .FindAsync(AirsideLabels.SystemContainers.Api, ct)
            .ConfigureAwait(false);

        var state = new UpdateState
        {
            UpdateId = record.Id,
            FromVersion = current,
            ToVersion = targetVersion,
            FromImageDigest = api?.ImageDigest,
            ToImageDigest = null,
            Step = UpdateStep.Starting,
            UpdatedAt = timeProvider.GetUtcNow(),
        };

        await WriteStateAsync(state, ct).ConfigureAwait(false);

        try
        {
            // Before anything is touched, so a failure at any later step still has
            // a snapshot of the instance as it was.
            state = state with { Step = UpdateStep.BackingUp, UpdatedAt = timeProvider.GetUtcNow() };
            await WriteStateAsync(state, ct).ConfigureAwait(false);

            record.Status = UpdateStatus.Downloading;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var backup = await backups.CreateAsync(options.BackupRoot, ct).ConfigureAwait(false);

            record.PreUpdateBackupPath = backup.ArchivePath;
            state = state with { BackupPath = backup.ArchivePath };

            // Pulled before the swap so the window with no control plane running
            // does not also contain a network download that might stall.
            state = state with { Step = UpdateStep.Pulling, UpdatedAt = timeProvider.GetUtcNow() };
            await WriteStateAsync(state, ct).ConfigureAwait(false);

            var target = new ImageReference(options.ImageRepository, targetVersion);

            // Airside's own image can live on a private registry — an
            // organisation mirroring it internally is a normal arrangement, and
            // without this the control plane would be the one thing that could
            // not be updated from where it is published.
            var auth = await scope.ServiceProvider
                .GetRequiredService<IRegistryCredentialSource>()
                .ResolveAsync(target, ct)
                .ConfigureAwait(false);

            var image = await runtime.Images
                .PullAsync(target, null, auth, ct)
                .ConfigureAwait(false);

            record.ToImageDigest = image.Digest;
            record.FromImageDigest = api?.ImageDigest;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            state = state with
            {
                ToImageDigest = image.Digest,
                Step = UpdateStep.Swapping,
                UpdatedAt = timeProvider.GetUtcNow(),
            };

            // The last thing written from inside the old container. Everything
            // after this is somebody else's problem — the detached updater's, or
            // failing that the operator's with the CLI.
            await WriteStateAsync(state, ct).ConfigureAwait(false);

            logger.LogWarning(
                "Update to {Version} is prepared. The control plane will now be replaced and will be "
                + "briefly unavailable.",
                targetVersion);

            return Result.Prepared;
        }
#pragma warning disable CA1031 // Any failure here must leave a recoverable state, not an exception.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            record.Status = UpdateStatus.Failed;
            record.ErrorCode = "update.prepare_failed";
            record.ErrorMessage = ex.Message;
            record.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await WriteStateAsync(
                state with
                {
                    Step = UpdateStep.Failed,
                    ErrorMessage = ex.Message,
                    UpdatedAt = timeProvider.GetUtcNow(),
                },
                ct).ConfigureAwait(false);

            await NotifyAsync(n => n.RaiseAsync(
                new NotificationRequest(
                    $"update.failed:{record.Id}",
                    NotificationLevel.Error,
                    $"The update to {targetVersion} could not be prepared",
                    $"{ex.Message} Nothing was changed — the running version is still {current}.",
                    "update.prepare_failed"),
                ct), ct).ConfigureAwait(false);

            logger.LogError(ex, "Preparing the update to {Version} failed; nothing was changed", targetVersion);

            return Result.Failed;
        }
    }

    /// <summary>
    /// Reconciles the update record with whatever actually happened.
    /// </summary>
    /// <remarks>
    /// Runs at startup, because the process that started the update is not the
    /// process that finishes it. If this instance is the new version and the state
    /// file says an update was in progress, the update worked; if it is the old
    /// version, the swap failed and something restarted the previous container.
    /// </remarks>
    public async Task ReconcileAsync(string runningVersion, CancellationToken ct)
    {
        var state = await ReadStateAsync(ct).ConfigureAwait(false);

        if (state is null || state.Step is UpdateStep.Succeeded or UpdateStep.RolledBack or UpdateStep.Failed)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

        var record = await db.UpdateRecords
            .FirstOrDefaultAsync(r => r.Id == state.UpdateId, ct)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var succeeded = string.Equals(runningVersion, state.ToVersion, StringComparison.Ordinal);

        if (record is not null)
        {
            record.Status = succeeded ? UpdateStatus.Succeeded : UpdateStatus.RolledBack;
            record.CompletedAt = now.UtcDateTime;

            if (!succeeded)
            {
                record.ErrorCode = "update.swap_failed";
                record.ErrorMessage =
                    $"The replacement container did not come up, so {state.FromVersion} is still running.";
            }

            var settings = await db.InstanceSettings.FirstAsync(ct).ConfigureAwait(false);

            if (succeeded)
            {
                settings.PreviousImageTag = state.FromVersion;
                settings.CurrentImageTag = state.ToVersion;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await WriteStateAsync(
            state with
            {
                Step = succeeded ? UpdateStep.Succeeded : UpdateStep.RolledBack,
                UpdatedAt = now,
            },
            ct).ConfigureAwait(false);

        if (succeeded)
        {
            logger.LogInformation("Update to {Version} completed", state.ToVersion);
            await NotifyAsync(n => n.ResolveAsync($"update.failed:{state.UpdateId}", ct), ct)
                .ConfigureAwait(false);

            return;
        }

        // Reached by starting up as the old version after an update was in
        // flight — which means the new one did not come up and something put the
        // old one back. Said plainly, because a silently reverted update looks
        // like an update that never ran.
        logger.LogError(
            "The update to {ToVersion} did not complete: this instance is running {Running}. The previous "
            + "version was restored.",
            state.ToVersion, runningVersion);

        await NotifyAsync(n => n.RaiseAsync(
            new NotificationRequest(
                $"update.failed:{state.UpdateId}",
                NotificationLevel.Error,
                $"The update to {state.ToVersion} was rolled back",
                $"The replacement did not become healthy, so {state.FromVersion} was restored. A backup "
                + $"from before the attempt is at {state.BackupPath ?? "the backup directory"}.",
                "update.swap_failed"),
            ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one notification inside its own scope.
    /// </summary>
    /// <remarks>
    /// The orchestrator is a singleton — it holds the state-file path and outlives
    /// any request — while the notifier is scoped because it writes through the
    /// DbContext. Capturing a scoped service in a singleton is the classic way to
    /// end up with one DbContext shared across the process, and the container
    /// refuses to build rather than let it happen.
    /// </remarks>
    private async Task NotifyAsync(Func<INotifier, Task> action, CancellationToken ct)
    {
        _ = ct;

        using var scope = scopeFactory.CreateScope();

        await action(scope.ServiceProvider.GetRequiredService<INotifier>()).ConfigureAwait(false);
    }

    public async Task WriteStateAsync(UpdateState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(Path.GetDirectoryName(options.StatePath)!);

        // Written to a temporary file and moved into place, so a process killed
        // mid-write leaves the previous state rather than a truncated file that
        // parses as nothing — which is precisely the moment this file matters.
        var temporary = options.StatePath + ".tmp";

        await File.WriteAllTextAsync(temporary, state.ToJson(), ct).ConfigureAwait(false);
        File.Move(temporary, options.StatePath, overwrite: true);
    }

    public async Task<UpdateState?> ReadStateAsync(CancellationToken ct)
    {
        if (!File.Exists(options.StatePath))
        {
            return null;
        }

        try
        {
            return UpdateState.FromJson(await File.ReadAllTextAsync(options.StatePath, ct).ConfigureAwait(false));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public enum Result
    {
        Prepared,
        AlreadyCurrent,
        Failed,
    }
}
