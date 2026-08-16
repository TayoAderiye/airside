using Airside.Core.Workloads;
using Airside.Core.Domains;
using System.Text.Json;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Naming;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Applications;
using Airside.Runtime.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Applications;

/// <summary>
/// The persistence side of the deployment handlers.
/// </summary>
/// <remarks>
/// Lives here so Airside.Runtime keeps its rule of never referencing EF Core.
/// This is also where the environment is rendered — from manual rows plus the
/// live credential of each attachment — so the handler receives a finished list
/// and never learns that injected variables are computed rather than stored.
/// </remarks>
internal sealed class ApplicationStore(
    AirsideDbContext db,
    ISecretProtector protector,
    IDatabaseEngineRegistry engines,
    EnvironmentRenderer renderer,
    TimeProvider timeProvider) : IApplicationStore
{
    public async Task<ApplicationSnapshot?> GetAsync(Guid applicationId, Guid deploymentId, CancellationToken ct)
    {
        var app = await db.Applications
            .Include(a => a.EnvironmentVariables)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct)
            .ConfigureAwait(false);

        if (app is null || !Slug.TryCreate(app.Slug, out var slug))
        {
            return null;
        }

        var deployment = await db.Deployments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
            .ConfigureAwait(false);

        var attachments = await LoadAttachmentsAsync(applicationId, ct).ConfigureAwait(false);

        var manual = new List<ManualVariable>();

        foreach (var variable in app.EnvironmentVariables)
        {
            if (!variable.IsSecret)
            {
                manual.Add(new ManualVariable(variable.Key, new Secret(variable.Value), false));
                continue;
            }

            var revealed = protector.Unprotect(variable.Value);

            if (revealed.IsSuccess)
            {
                manual.Add(new ManualVariable(variable.Key, revealed.Value, true));
            }
        }

        var rendered = renderer.Render(manual, attachments.Select(a => a.Attached).ToList());

        // A rollback names a target deployment whose digest is already known, so
        // the source becomes "this exact image" rather than whatever the
        // application's configured source would rebuild.
        var source = deployment?.TriggerKind == DeploymentTrigger.Rollback && deployment.ImageDigest is not null
            ? DeploymentSource.ExistingDigest
            : app.SourceKind switch
            {
                ApplicationSourceKind.Image => DeploymentSource.Image,
                ApplicationSourceKind.Git => DeploymentSource.Git,
                _ => DeploymentSource.Dockerfile,
            };

        var imageRef = source == DeploymentSource.ExistingDigest ? deployment!.ImageDigest : app.SourceImageRef;

        return new ApplicationSnapshot(
            app.Id,
            slug,
            app.DisplayName,
            source,
            imageRef,
            app.GitRepositoryUrl,
            app.GitBranch,
            app.DockerfilePath,
            app.DockerfileContent,
            app.ContainerPort,
            BuildHealthProbe(app),
            app.CpuLimitNanos,
            app.MemoryLimitBytes,
            app.AutoRestart,
            AirsideNames.ApplicationNetwork(slug),
            app.ContainerId,
            rendered.Entries,
            [.. attachments.Select(a => a.NetworkName)],
            await db.Domains
                .AsNoTracking()
                .Where(d => d.ApplicationId == applicationId && d.Status != DomainStatus.Failed)
                .Select(d => d.Hostname)
                .ToListAsync(ct)
                .ConfigureAwait(false));
    }

    private async Task<List<(AttachedDatabase Attached, string NetworkName)>> LoadAttachmentsAsync(
        Guid applicationId,
        CancellationToken ct)
    {
        var rows = await db.DatabaseAttachments
            .AsNoTracking()
            .Where(a => a.ApplicationId == applicationId && a.DetachedAt == null)
            .Join(db.Databases, a => a.DatabaseInstanceId, d => d.Id, (a, d) => new { Attachment = a, Database = d })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<(AttachedDatabase, string)>();

        foreach (var row in rows)
        {
            var credential = await db.DatabaseCredentials
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == row.Attachment.CredentialId, ct)
                .ConfigureAwait(false);

            // Deliberately re-read on every render, and deliberately the *live*
            // primary rather than the one recorded on the attachment: a rotation
            // must reach the application on its next deploy without anyone
            // editing an environment variable.
            credential = await db.DatabaseCredentials
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    c => c.DatabaseInstanceId == row.Database.Id && c.IsPrimary && c.State == CredentialState.Active,
                    ct)
                .ConfigureAwait(false) ?? credential;

            if (credential is null)
            {
                continue;
            }

            var password = protector.Unprotect(credential.EncryptedPassword);

            if (password.IsFailure || !Slug.TryCreate(row.Database.Slug, out var dbSlug))
            {
                continue;
            }

            var engine = engines.Get(row.Database.Engine);

            result.Add((
                new AttachedDatabase(
                    row.Attachment.Id,
                    row.Database.Engine,
                    row.Attachment.EnvKeyPrefix,
                    new DatabaseEndpoint(
                        row.Database.ContainerId ?? string.Empty,
                        AirsideNames.DatabaseContainer(dbSlug),
                        engine.Capabilities.DefaultPort,
                        row.Database.DatabaseName),
                    new DatabaseCredentialValue(credential.Username, password.Value)),
                AirsideNames.DatabaseNetwork(dbSlug)));
        }

        return result;
    }

    /// <summary>
    /// Turns the stored health-check configuration into a container probe.
    /// </summary>
    /// <remarks>
    /// The HTTP form is expressed as a <c>wget</c> argument vector rather than a
    /// shell line, because there is no shell anywhere in Airside — and because a
    /// path is user-supplied, which is exactly the value that must never be able
    /// to become a command.
    /// </remarks>
    private static HealthProbe BuildHealthProbe(Application app)
    {
        IReadOnlyList<string> command = app.HealthCheckKind == HealthCheckKind.Command
            ? JsonSerializer.Deserialize<string[]>(app.HealthCheckCommandJson ?? "[]") ?? []
            :
            [
                "wget", "--quiet", "--tries=1", "--spider",
                $"http://127.0.0.1:{app.ContainerPort}{app.HealthCheckPath ?? "/"}",
            ];

        return new HealthProbe(
            command,
            TimeSpan.FromSeconds(app.HealthCheckIntervalSeconds),
            TimeSpan.FromSeconds(app.HealthCheckTimeoutSeconds),
            app.HealthCheckRetries,
            TimeSpan.FromSeconds(10));
    }

    public async Task SetStateAsync(Guid applicationId, string state, CancellationToken ct)
    {
        var app = await db.Workloads.FirstOrDefaultAsync(w => w.Id == applicationId, ct).ConfigureAwait(false);

        if (app is null)
        {
            return;
        }

        app.State = state;
        app.StateChangedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordDeploymentStartedAsync(Guid deploymentId, CancellationToken ct)
    {
        var deployment = await db.Deployments
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct).ConfigureAwait(false);

        if (deployment is null)
        {
            return;
        }

        deployment.Status = DeploymentStatus.Building;
        deployment.StartedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordDeploymentSucceededAsync(
        Guid deploymentId,
        string imageRef,
        string? imageDigest,
        string containerId,
        CancellationToken ct)
    {
        var deployment = await db.Deployments
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct).ConfigureAwait(false);

        if (deployment is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        deployment.Status = DeploymentStatus.Succeeded;
        deployment.ImageRef = imageRef;
        deployment.ImageDigest = imageDigest;
        deployment.ContainerId = containerId;
        deployment.CompletedAt = now;
        deployment.DurationMs = (int)(now - deployment.StartedAt).TotalMilliseconds;
        deployment.IsCurrent = true;

        // Exactly one current deployment. Two would make "roll back to the
        // previous one" ambiguous.
        var others = await db.Deployments
            .Where(d => d.ApplicationId == deployment.ApplicationId && d.Id != deploymentId && d.IsCurrent)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        others.ForEach(d => d.IsCurrent = false);

        var app = await db.Applications
            .FirstOrDefaultAsync(a => a.Id == deployment.ApplicationId, ct).ConfigureAwait(false);

        if (app is not null)
        {
            app.CurrentDeploymentId = deploymentId;
            app.ContainerId = containerId;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordDeploymentFailedAsync(
        Guid deploymentId,
        string code,
        string message,
        CancellationToken ct)
    {
        var deployment = await db.Deployments
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct).ConfigureAwait(false);

        if (deployment is null || deployment.Status == DeploymentStatus.Succeeded)
        {
            return;
        }

        deployment.Status = DeploymentStatus.Failed;
        deployment.ErrorCode = code;
        deployment.ErrorMessage = message.Length > 1024 ? message[..1024] : message;
        deployment.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AppendBuildLogAsync(Guid deploymentId, string log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(log))
        {
            return;
        }

        var (content, truncated) = BuildLog.Cap(log);

        var existing = await db.DeploymentLogs
            .FirstOrDefaultAsync(l => l.DeploymentId == deploymentId, ct).ConfigureAwait(false);

        if (existing is null)
        {
            db.DeploymentLogs.Add(new DeploymentLog
            {
                DeploymentId = deploymentId,
                Content = content,
                Truncated = truncated,
                ByteCount = System.Text.Encoding.UTF8.GetByteCount(content),
            });
        }
        else
        {
            existing.Content = content;
            existing.Truncated = truncated;
            existing.ByteCount = System.Text.Encoding.UTF8.GetByteCount(content);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetDeploymentDigestAsync(Guid deploymentId, CancellationToken ct) =>
        await db.Deployments
            .AsNoTracking()
            .Where(d => d.Id == deploymentId && d.Status == DeploymentStatus.Succeeded)
            .Select(d => d.ImageDigest)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<AttachmentTarget?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct)
    {
        var attachment = await db.DatabaseAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct)
            .ConfigureAwait(false);

        if (attachment is null)
        {
            return null;
        }

        var app = await db.Applications
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachment.ApplicationId, ct)
            .ConfigureAwait(false);

        var database = await db.Databases
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == attachment.DatabaseInstanceId, ct)
            .ConfigureAwait(false);

        if (app is null || database is null || !Slug.TryCreate(database.Slug, out var dbSlug))
        {
            return null;
        }

        return new AttachmentTarget(app.Id, app.ContainerId, AirsideNames.DatabaseNetwork(dbSlug));
    }

    public async Task RecordAttachmentAppliedAsync(Guid attachmentId, CancellationToken ct)
    {
        var attachment = await db.DatabaseAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct).ConfigureAwait(false);

        if (attachment is not null && attachment.AttachedAt == default)
        {
            attachment.AttachedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Reads and writes for the application lifecycle and teardown paths.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="ApplicationStore"/> because deleting needs to
/// gather everything Airside created <em>before</em> any of it is destroyed —
/// once the container is gone, so is the only record of which network and volumes
/// belonged to it.
/// </remarks>
internal sealed class ApplicationLifecycleStore(
    AirsideDbContext db,
    TimeProvider timeProvider) : IApplicationLifecycleStore
{
    public async Task<ApplicationTeardown?> GetTeardownAsync(Guid applicationId, CancellationToken ct)
    {
        var app = await db.Applications
            .AsNoTracking()
            .Include(a => a.Volumes)
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct)
            .ConfigureAwait(false);

        if (app is null || !Slug.TryCreate(app.Slug, out var slug))
        {
            return null;
        }

        var hostnames = await db.Domains
            .AsNoTracking()
            .Where(d => d.ApplicationId == applicationId)
            .Select(d => d.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var images = await db.Deployments
            .AsNoTracking()
            .Where(d => d.ApplicationId == applicationId && d.ImageDigest != null)
            .Select(d => d.ImageDigest!)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new ApplicationTeardown(
            app.Id,
            slug,
            app.ContainerId,
            AirsideNames.ApplicationNetwork(slug),
            hostnames,
            [.. app.Volumes.Select(v => v.Name)],
            images);
    }

    public async Task<string?> GetContainerIdAsync(Guid applicationId, CancellationToken ct) =>
        await db.Applications
            .AsNoTracking()
            .Where(a => a.Id == applicationId)
            .Select(a => a.ContainerId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task SetLifecycleStateAsync(Guid applicationId, string state, CancellationToken ct)
    {
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == applicationId, ct).ConfigureAwait(false);

        if (app is null)
        {
            return;
        }

        app.State = state;
        app.StateChangedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkDeletedAsync(Guid applicationId, CancellationToken ct)
    {
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == applicationId, ct).ConfigureAwait(false);

        if (app is null)
        {
            return;
        }

        app.State = nameof(ApplicationState.Deleted);
        app.StateChangedAt = timeProvider.GetUtcNow().UtcDateTime;
        app.DeletedAt = timeProvider.GetUtcNow().UtcDateTime;
        app.ContainerId = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Soft-deletes every domain on the application so the hostnames are free again.
    /// </summary>
    /// <remarks>
    /// Deliberately not a cascade from the application row. A domain is a
    /// hostname somebody configured DNS for, and the delete has to be an explicit,
    /// audited act rather than a side effect of a foreign key.
    /// </remarks>
    public async Task ReleaseDomainsAsync(Guid applicationId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var domains = await db.Domains
            .Where(d => d.ApplicationId == applicationId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var domain in domains)
        {
            domain.DetachedAt = now;
            domain.DeletedAt = now;
            domain.Status = DomainStatus.Detaching;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
