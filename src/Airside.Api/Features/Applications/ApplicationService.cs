using System.Text.Json;
using Airside.Api.Contracts;
using Airside.Api.Features.Databases;
using Airside.Api.Hosting;
using Airside.Core.Common;
using Airside.Core.Databases;
using Airside.Core.Hosting;
using Airside.Core.Jobs;
using Airside.Core.Naming;
using Airside.Core.Security;
using Airside.Core.Workloads;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Applications;
using Airside.Runtime.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Applications;

public sealed class ApplicationService(
    AirsideDbContext db,
    IDatabaseEngineRegistry engines,
    EnvironmentRenderer renderer,
    IAllocationPolicy allocationPolicy,
    IHostAllocationReader allocationReader,
    AllocationGate gate,
    ISecretProtector protector,
    Airside.Core.Containers.IContainerRuntime runtime,
    IJobQueue jobs,
    TimeProvider timeProvider)
{
    public async Task<Result<Guid>> CreateAsync(
        CreateApplicationRequest request,
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

        if (!Enum.TryParse<ApplicationSourceKind>(request.SourceKind, ignoreCase: true, out var source))
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                $"'{request.SourceKind}' is not a supported source. Airside builds from an image, a Git "
                + "repository containing a Dockerfile, or an inline Dockerfile. Compose is out of scope.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field"] = "sourceKind",
                    ["supported"] = SupportedSources,
                });
        }

        var sourceValidation = ValidateSource(source, request);

        if (sourceValidation.IsFailure)
        {
            return sourceValidation.Failure!;
        }

        var health = ValidateHealthCheck(request.HealthCheck);

        if (health.IsFailure)
        {
            return health.Failure!;
        }

        if (request.ContainerPort is < 1 or > 65535)
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "containerPort must be between 1 and 65535.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "containerPort" });
        }

        return await gate.WithExclusiveAccessAsync(async () =>
        {
            if (await db.Workloads.AnyAsync(w => w.Slug == slug.Value, ct).ConfigureAwait(false))
            {
                return (Result<Guid>)new Error(
                    ErrorCodes.WorkloadSlugTaken, $"A workload named '{slug.Value}' already exists.");
            }

            var position = await allocationReader.ReadPositionAsync(ct).ConfigureAwait(false);
            var admission = allocationPolicy.Admit(
                position, new ResourceTriple(request.CpuNanos, request.MemoryBytes, request.StorageBytes));

            if (admission.IsFailure)
            {
                return (Result<Guid>)admission.Failure!;
            }

            var host = await db.Hosts.FirstAsync(ct).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow().UtcDateTime;

            var application = new Application
            {
                HostId = host.Id,
                Kind = WorkloadKind.Application,
                Slug = slug.Value,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? slug.Value : request.DisplayName,

                // Created, not Building. Creating an application writes a record
                // and allocates nothing on the host; deploying is a separate,
                // explicit act, so a typo in a Dockerfile path can be corrected
                // without deleting and starting over.
                State = ApplicationState.Created.ToString(),
                StateChangedAt = now,
                CpuLimitNanos = request.CpuNanos,
                MemoryLimitBytes = request.MemoryBytes,
                StorageAllocationBytes = request.StorageBytes,
                AutoRestart = request.AutoRestart,
                NetworkName = AirsideNames.ApplicationNetwork(slug),
                CreatedByUserId = userId,
                SourceKind = source,
                SourceImageRef = request.ImageRef,
                GitRepositoryUrl = request.GitRepositoryUrl,
                GitBranch = request.GitBranch,
                DockerfilePath = request.DockerfilePath,
                DockerfileContent = request.DockerfileContent,
                ContainerPort = request.ContainerPort,
                HealthCheckKind = health.Value,
                HealthCheckPath = request.HealthCheck.Path,
                HealthCheckExpectedStatus = request.HealthCheck.ExpectedStatus,
                HealthCheckCommandJson = request.HealthCheck.Command is null
                    ? null
                    : JsonSerializer.Serialize(request.HealthCheck.Command),
                HealthCheckIntervalSeconds = request.HealthCheck.IntervalSeconds,
                HealthCheckTimeoutSeconds = request.HealthCheck.TimeoutSeconds,
                HealthCheckRetries = request.HealthCheck.Retries,
            };

            db.Applications.Add(application);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return (Result<Guid>)application.Id;
        }, ct).ConfigureAwait(false);
    }

    private static Result ValidateSource(ApplicationSourceKind source, CreateApplicationRequest request) =>
        source switch
        {
            ApplicationSourceKind.Image when string.IsNullOrWhiteSpace(request.ImageRef) =>
                Missing("imageRef", "An image source needs an image reference."),

            ApplicationSourceKind.Git when GitSource.ValidateUrl(request.GitRepositoryUrl).IsFailure =>
                GitSource.ValidateUrl(request.GitRepositoryUrl).Failure!,

            ApplicationSourceKind.Git when
                BuildContextPaths.ResolveWithin("/build", request.DockerfilePath ?? "Dockerfile").IsFailure =>
                BuildContextPaths.ResolveWithin("/build", request.DockerfilePath ?? "Dockerfile").Failure!,

            ApplicationSourceKind.Dockerfile when string.IsNullOrWhiteSpace(request.DockerfileContent) =>
                Missing("dockerfileContent", "A Dockerfile source needs the Dockerfile itself."),

            _ => Result.Ok(),
        };

    /// <summary>
    /// Validates the health check, which is mandatory.
    /// </summary>
    /// <remarks>
    /// A command check must be an argument vector. Accepting a string and
    /// splitting it would put Airside in the business of parsing shell syntax,
    /// which is the thing every other part of the system avoids.
    /// </remarks>
    private static Result<HealthCheckKind> ValidateHealthCheck(HealthCheckRequest request)
    {
        if (!Enum.TryParse<HealthCheckKind>(request.Kind, ignoreCase: true, out var kind))
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "A health check must be 'http' or 'command'. There is no 'none': without a health check, "
                + "a zero-downtime deploy is just a pause and a hope.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "healthCheck.kind" });
        }

        if (kind == HealthCheckKind.Http && string.IsNullOrWhiteSpace(request.Path))
        {
            return Missing("healthCheck.path", "An HTTP health check needs a path.").Failure!;
        }

        if (kind == HealthCheckKind.Command && (request.Command is null || request.Command.Count == 0))
        {
            return Missing(
                "healthCheck.command",
                "A command health check needs an argument vector, for example [\"/bin/healthcheck\", \"--fast\"].")
                .Failure!;
        }

        return kind;
    }

    public async Task<Result<JobAccepted>> DeployAsync(
        Guid id,
        DeployRequest request,
        Guid? userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);

        if (app is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such application.");
        }

        var deployment = await NewDeploymentAsync(app, DeploymentTrigger.Manual, request.Branch, null, userId, ct)
            .ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            ApplicationJobTypes.Deploy,
            new DeployPayload(id, deployment.Id),
            id,
            userId,
            $"{ApplicationJobTypes.Deploy}:{deployment.Id}",
            ct).ConfigureAwait(false);

        return JobAccepted.From(jobId, ApplicationJobTypes.Deploy, id);
    }

    public async Task<Result<JobAccepted>> RollbackAsync(Guid deploymentId, Guid? userId, CancellationToken ct)
    {
        var target = await db.Deployments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
            .ConfigureAwait(false);

        if (target is null)
        {
            return new Error(ErrorCodes.DeploymentNotFound, "No such deployment.");
        }

        if (target.Status != DeploymentStatus.Succeeded || string.IsNullOrEmpty(target.ImageDigest))
        {
            return new Error(
                ErrorCodes.DeploymentNotFound,
                "Only a successful deployment that recorded an image digest can be rolled back to.");
        }

        // Checked before enqueuing. A rollback whose image has been pruned should
        // say so immediately, not accept the request and fail a minute later —
        // and it must never quietly rebuild from source, which would produce a
        // different artefact than the one being rolled back to.
        var present = await runtime.Images
            .FindAsync(Airside.Core.Containers.ImageReference.Parse(target.ImageDigest), ct)
            .ConfigureAwait(false);

        if (present is null)
        {
            return new Error(
                ErrorCodes.DeploymentImagePruned,
                "The image for that deployment is no longer on this host, so it cannot be rolled back to. "
                + "Deploy again from source instead — that produces a new build, not the old one.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["deploymentNumber"] = target.Number,
                    ["imageDigest"] = target.ImageDigest,
                });
        }

        var app = await db.Applications.FirstAsync(a => a.Id == target.ApplicationId, ct).ConfigureAwait(false);

        // Recorded as a new deployment rather than reactivating the old row, so
        // history stays linear: "we rolled back to 12" is deployment 15.
        var deployment = await NewDeploymentAsync(
            app, DeploymentTrigger.Rollback, target.Branch, target.ImageDigest, userId, ct).ConfigureAwait(false);

        deployment.RolledBackFromDeploymentId = app.CurrentDeploymentId;
        deployment.CommitSha = target.CommitSha;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            ApplicationJobTypes.Deploy,
            new DeployPayload(app.Id, deployment.Id),
            app.Id,
            userId,
            $"{ApplicationJobTypes.Deploy}:{deployment.Id}",
            ct).ConfigureAwait(false);

        return JobAccepted.From(jobId, ApplicationJobTypes.Rollback, app.Id);
    }

    private async Task<Deployment> NewDeploymentAsync(
        Application app,
        DeploymentTrigger trigger,
        string? branch,
        string? imageDigest,
        Guid? userId,
        CancellationToken ct)
    {
        var lastNumber = await db.Deployments
            .Where(d => d.ApplicationId == app.Id)
            .Select(d => (int?)d.Number)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0;

        var deployment = new Deployment
        {
            ApplicationId = app.Id,
            Number = lastNumber + 1,
            Status = DeploymentStatus.Queued,
            TriggerKind = trigger,
            SourceKindSnapshot = app.SourceKind,
            Branch = branch ?? app.GitBranch,
            ImageDigest = imageDigest,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
            TriggeredByUserId = userId,
        };

        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return deployment;
    }

    /// <summary>
    /// Attaches a database, which is simultaneously the network authorisation and
    /// the source of the injected environment.
    /// </summary>
    public async Task<Result<JobAccepted>> AttachAsync(
        Guid applicationId,
        AttachDatabaseRequest request,
        Guid? userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var app = await db.Applications
            .Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct)
            .ConfigureAwait(false);

        var database = await db.Databases
            .Include(d => d.Credentials)
            .FirstOrDefaultAsync(d => d.Id == request.DatabaseId, ct)
            .ConfigureAwait(false);

        if (app is null || database is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such application or database.");
        }

        var engine = engines.Get(database.Engine);
        var prefix = string.IsNullOrWhiteSpace(request.EnvKeyPrefix)
            ? engine.Capabilities.DefaultEnvKeyPrefix
            : request.EnvKeyPrefix.ToUpperInvariant();

        var live = await db.DatabaseAttachments
            .Where(a => a.ApplicationId == applicationId && a.DetachedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (live.Exists(a => a.DatabaseInstanceId == request.DatabaseId))
        {
            return new Error(
                ErrorCodes.DatabaseAttachmentExists, "That database is already attached to this application.");
        }

        // Two attached Postgres databases cannot both claim DATABASE_URL.
        if (live.Exists(a => string.Equals(a.EnvKeyPrefix, prefix, StringComparison.Ordinal)))
        {
            return new Error(
                ErrorCodes.DatabaseEnvPrefixConflict,
                $"Another attached database already uses the '{prefix}' prefix. Choose a different one, "
                + "for example 'ANALYTICS', so the injected variables do not collide.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["prefix"] = prefix });
        }

        // A manual variable already occupying an injected key would be silently
        // shadowed at deploy, so the collision is refused where it can be explained.
        var injected = renderer.InjectedKeysFor(database.Engine, prefix);
        var clash = app.EnvironmentVariables.FirstOrDefault(v => injected.Contains(v.Key, StringComparer.Ordinal));

        if (clash is not null)
        {
            return new Error(
                ErrorCodes.EnvironmentKeyConflict,
                $"This application already sets '{clash.Key}' manually, which this attachment would inject. "
                + "Remove the manual variable or choose a different prefix.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = clash.Key });
        }

        var credential = database.Credentials.FirstOrDefault(c => c.IsPrimary && c.State == CredentialState.Active);

        if (credential is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "That database has no active credential.");
        }

        var attachment = new DatabaseAttachment
        {
            ApplicationId = applicationId,
            DatabaseInstanceId = request.DatabaseId,
            EnvKeyPrefix = prefix,
            CredentialId = credential.Id,
            AttachedByUserId = userId,
        };

        db.DatabaseAttachments.Add(attachment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            ApplicationJobTypes.AttachDatabase,
            new AttachmentPayload(applicationId, attachment.Id, Attach: true),
            applicationId,
            userId,
            $"{ApplicationJobTypes.AttachDatabase}:{attachment.Id}",
            ct).ConfigureAwait(false);

        return JobAccepted.From(jobId, ApplicationJobTypes.AttachDatabase, applicationId);
    }

    public async Task<Result<JobAccepted>> DetachAsync(
        Guid applicationId,
        Guid attachmentId,
        Guid? userId,
        CancellationToken ct)
    {
        var attachment = await db.DatabaseAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.ApplicationId == applicationId, ct)
            .ConfigureAwait(false);

        if (attachment is null || attachment.DetachedAt is not null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such live attachment.");
        }

        // Soft close, not delete: "who gave this app access to the customer
        // database" has to survive the access being taken away.
        attachment.DetachedAt = timeProvider.GetUtcNow().UtcDateTime;
        attachment.DetachedByUserId = userId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            ApplicationJobTypes.AttachDatabase,
            new AttachmentPayload(applicationId, attachmentId, Attach: false),
            applicationId,
            userId,
            $"{ApplicationJobTypes.DetachDatabase}:{attachmentId}",
            ct).ConfigureAwait(false);

        return JobAccepted.From(jobId, ApplicationJobTypes.DetachDatabase, applicationId);
    }

    /// <summary>
    /// Sets a manual environment variable.
    /// </summary>
    /// <remarks>
    /// Refused when the key is one an attachment injects. Allowing it would let a
    /// manual value shadow the connection details for a database the application
    /// is attached to, while the attachment screen went on claiming otherwise.
    /// </remarks>
    public async Task<Result> SetEnvironmentAsync(
        Guid applicationId,
        string key,
        SetEnvironmentRequest request,
        Guid? userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var app = await db.Applications
            .Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct)
            .ConfigureAwait(false);

        if (app is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such application.");
        }

        if (!EnvironmentKeyPattern.IsMatch(key))
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "An environment key must match ^[A-Z_][A-Z0-9_]*$.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key });
        }

        var attachments = await db.DatabaseAttachments
            .AsNoTracking()
            .Where(a => a.ApplicationId == applicationId && a.DetachedAt == null)
            .Join(db.Databases, a => a.DatabaseInstanceId, d => d.Id, (a, d) => new { a.EnvKeyPrefix, d.Engine })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var attachment in attachments)
        {
            if (renderer.InjectedKeysFor(attachment.Engine, attachment.EnvKeyPrefix)
                .Contains(key, StringComparer.Ordinal))
            {
                return new Error(
                    ErrorCodes.EnvironmentKeyReserved,
                    $"'{key}' is injected by an attached database and cannot be set manually. Detach the "
                    + "database or change its key prefix if you need this name.",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key });
            }
        }

        var existing = app.EnvironmentVariables.FirstOrDefault(v => v.Key == key);
        var stored = request.IsSecret ? protector.Protect(new Secret(request.Value)) : request.Value;

        if (existing is null)
        {
            db.EnvironmentVariables.Add(new EnvironmentVariable
            {
                ApplicationId = applicationId,
                Key = key,
                Value = stored,
                IsSecret = request.IsSecret,
                UpdatedByUserId = userId,
            });
        }
        else
        {
            existing.Value = stored;
            existing.IsSecret = request.IsSecret;
            existing.UpdatedByUserId = userId;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Ok();
    }

    private static readonly string[] SupportedSources = ["image", "git", "dockerfile"];

    private static readonly System.Text.RegularExpressions.Regex EnvironmentKeyPattern =
        new("^[A-Z_][A-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static Result Missing(string field, string message) => new Error(
        ErrorCodes.ValidationFieldRequired,
        message,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = field });
}
