using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Jobs;
using Airside.Core.Naming;
using Airside.Core.Workloads;
using Airside.Runtime.Applications;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Jobs;

public static class ApplicationJobTypes
{
    public const string Deploy = "application.deploy";
    public const string Rollback = "application.rollback";
    public const string Delete = "application.delete";
    public const string AttachDatabase = "application.attach_database";
    public const string DetachDatabase = "application.detach_database";
}

public sealed record DeployPayload(Guid WorkloadId, Guid DeploymentId);

public sealed record RollbackPayload(Guid WorkloadId, Guid TargetDeploymentId, Guid NewDeploymentId);

public sealed record AttachmentPayload(Guid ApplicationId, Guid AttachmentId, bool Attach);

/// <summary>How an application's image is obtained for a deployment.</summary>
public enum DeploymentSource
{
    /// <summary>Pull an existing image; nothing is built.</summary>
    Image,

    /// <summary>Clone a repository and build its Dockerfile.</summary>
    Git,

    /// <summary>Build a Dockerfile supplied inline.</summary>
    Dockerfile,

    /// <summary>Re-run a previously built image, resolved by digest.</summary>
    ExistingDigest,
}

/// <summary>An application flattened into what a deployment needs.</summary>
public sealed record ApplicationSnapshot(
    Guid Id,
    Slug Slug,
    string DisplayName,
    DeploymentSource Source,
    string? ImageRef,
    string? GitRepositoryUrl,
    string? GitBranch,
    string? DockerfilePath,
    string? DockerfileContent,
    int ContainerPort,
    HealthProbe HealthProbe,
    long CpuNanos,
    long MemoryBytes,
    bool AutoRestart,
    string NetworkName,
    string? CurrentContainerId,
    IReadOnlyList<EnvironmentEntry> Environment,
    IReadOnlyList<string> AttachedNetworks);

public interface IApplicationStore
{
    Task<ApplicationSnapshot?> GetAsync(Guid applicationId, Guid deploymentId, CancellationToken ct);

    Task SetStateAsync(Guid applicationId, string state, CancellationToken ct);

    Task RecordDeploymentStartedAsync(Guid deploymentId, CancellationToken ct);

    Task RecordDeploymentSucceededAsync(
        Guid deploymentId,
        string imageRef,
        string? imageDigest,
        string containerId,
        CancellationToken ct);

    Task RecordDeploymentFailedAsync(Guid deploymentId, string code, string message, CancellationToken ct);

    Task AppendBuildLogAsync(Guid deploymentId, string log, CancellationToken ct);

    /// <summary>The image digest of a previous deployment, for rollback.</summary>
    Task<string?> GetDeploymentDigestAsync(Guid deploymentId, CancellationToken ct);

    Task<AttachmentTarget?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct);

    Task RecordAttachmentAppliedAsync(Guid attachmentId, CancellationToken ct);
}

public sealed record AttachmentTarget(
    Guid ApplicationId,
    string? ApplicationContainerId,
    string DatabaseNetworkName);

/// <summary>
/// Builds and releases a new version of an application.
/// </summary>
/// <remarks>
/// <para>
/// The sequence is build, create, start, poll health, then stop the old
/// container. The old one keeps serving until the new one is confirmed healthy,
/// which is the whole of "zero downtime" at the container level — the proxy
/// upstream swap that completes it arrives with Caddy in Phase 5, and until then
/// applications are reachable only from inside their own network.
/// </para>
/// <para>
/// Nothing about the previous deployment is touched until the new one passes its
/// health check, so a failed deploy leaves the running version exactly where it
/// was.
/// </para>
/// </remarks>
public sealed class DeployHandler(
    IContainerRuntime runtime,
    IApplicationStore store,
    GitSource git,
    ILogger<DeployHandler> logger) : IJobHandler
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public string JobType => ApplicationJobTypes.Deploy;

    public async Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.GetPayload<DeployPayload>();
        var app = await store.GetAsync(payload.WorkloadId, payload.DeploymentId, ct).ConfigureAwait(false);

        if (app is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "The application no longer exists.");
        }

        await store.RecordDeploymentStartedAsync(payload.DeploymentId, ct).ConfigureAwait(false);
        await store.SetStateAsync(app.Id, ApplicationState.Building.ToString(), ct).ConfigureAwait(false);

        var buildLog = new System.Text.StringBuilder();
        var progress = new Progress<string>(line => buildLog.Append(line));

        ImageSummary image;

        try
        {
            image = await ObtainImageAsync(context, app, payload.DeploymentId, progress, ct).ConfigureAwait(false);
        }
        catch (DeploymentImageMissingException ex)
        {
            await store.RecordDeploymentFailedAsync(
                payload.DeploymentId, ErrorCodes.DeploymentImagePruned, ex.Message, ct).ConfigureAwait(false);

            return new Error(ErrorCodes.DeploymentImagePruned, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await store.AppendBuildLogAsync(payload.DeploymentId, buildLog.ToString(), ct).ConfigureAwait(false);
            await store.RecordDeploymentFailedAsync(
                payload.DeploymentId, ErrorCodes.ApplicationBuildFailed, ex.Message, ct).ConfigureAwait(false);

            return new Error(ErrorCodes.ApplicationBuildFailed, ex.Message);
        }
        finally
        {
            await store.AppendBuildLogAsync(payload.DeploymentId, buildLog.ToString(), ct).ConfigureAwait(false);
        }

        await context.ReportProgressAsync(45, "Preparing the network", ct).ConfigureAwait(false);
        await EnsureNetworkAsync(context, app, ct).ConfigureAwait(false);

        await context.ReportProgressAsync(50, "Creating container", ct).ConfigureAwait(false);
        await store.SetStateAsync(app.Id, ApplicationState.Deploying.ToString(), ct).ConfigureAwait(false);

        var containerName = AirsideNames.ApplicationContainer(app.Slug, payload.DeploymentId);
        var containerId = await CreateContainerAsync(context, app, image, containerName, ct).ConfigureAwait(false);

        await context.ReportProgressAsync(65, "Starting", ct).ConfigureAwait(false);
        await runtime.Containers.StartAsync(containerId, ct).ConfigureAwait(false);

        // Attached database networks are joined after start, which is a live
        // Docker operation and needs no restart.
        foreach (var network in app.AttachedNetworks)
        {
            await runtime.Networks.ConnectAsync(network, containerId, ct).ConfigureAwait(false);
        }

        await context.ReportProgressAsync(75, "Waiting for health check", ct).ConfigureAwait(false);

        if (!await WaitForHealthAsync(context, containerId, ct).ConfigureAwait(false))
        {
            await store.RecordDeploymentFailedAsync(
                payload.DeploymentId,
                ErrorCodes.ApplicationHealthCheckFailed,
                "The new container never became healthy. The previous version is still running.",
                ct).ConfigureAwait(false);

            return new Error(
                ErrorCodes.ApplicationHealthCheckFailed,
                "The new container never became healthy and was removed. The previous version is "
                + "still serving traffic.");
        }

        // Only now is the old container touched. Everything above could fail
        // without the running version noticing.
        if (app.CurrentContainerId is { } previous && previous != containerId)
        {
            await context.ReportProgressAsync(90, "Stopping the previous version", ct).ConfigureAwait(false);
            await runtime.Containers.StopAsync(previous, StopTimeout, ct).ConfigureAwait(false);
            await runtime.Containers.RemoveAsync(previous, force: false, ct).ConfigureAwait(false);

            await context.LogStepAsync("cutover", "Previous container stopped and removed.", ct)
                .ConfigureAwait(false);
        }

        await store.RecordDeploymentSucceededAsync(
            payload.DeploymentId, image.Tags.Count > 0 ? image.Tags[0] : string.Empty, image.Digest, containerId, ct)
            .ConfigureAwait(false);

        await store.SetStateAsync(app.Id, ApplicationState.Running.ToString(), ct).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Running", ct).ConfigureAwait(false);

        return Result.Ok();
    }

    /// <summary>
    /// Creates the application's own network if it is not already there.
    /// </summary>
    /// <remarks>
    /// Every application gets its own network, which is what makes isolation
    /// pairwise: the proxy joins it to route, and attached databases are reached
    /// on theirs. It is created on first deploy rather than at application
    /// creation, so an application that is never deployed leaves nothing behind on
    /// the host — and it is tracked as pre-existing on later deploys so a failed
    /// redeploy cannot remove the network the running version is using.
    /// </remarks>
    private async Task EnsureNetworkAsync(IJobContext context, ApplicationSnapshot app, CancellationToken ct)
    {
        var existing = await runtime.Networks.FindAsync(app.NetworkName, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            await context.TrackResourceAsync(JobResourceKind.Network, app.NetworkName, false, ct)
                .ConfigureAwait(false);
            return;
        }

        await runtime.Networks
            .CreateAsync(new NetworkSpec(app.NetworkName, Labels(app)), ct)
            .ConfigureAwait(false);

        await context.TrackResourceAsync(JobResourceKind.Network, app.NetworkName, true, ct)
            .ConfigureAwait(false);
    }

    private async Task<ImageSummary> ObtainImageAsync(
        IJobContext context,
        ApplicationSnapshot app,
        Guid deploymentId,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var labels = Labels(app);

        switch (app.Source)
        {
            case DeploymentSource.ExistingDigest:
            {
                // A rollback resolves locally and never pulls. The image was built
                // here and exists in no registry, so a pull would fail with a
                // registry error that says nothing about the real problem — and if
                // the image has been pruned, the honest answer is that this
                // rollback is no longer possible, not a silent rebuild from source
                // that would produce a different artefact wearing the same number.
                await context.ReportProgressAsync(15, "Resolving the previous image", ct).ConfigureAwait(false);

                var previous = await runtime.Images
                    .FindAsync(ImageReference.Parse(app.ImageRef!), ct)
                    .ConfigureAwait(false);

                return previous ?? throw new DeploymentImageMissingException(
                    "The image for that deployment is no longer on this host, so it cannot be rolled back "
                    + "to. Deploy again from source instead — that produces a new build, not the old one.");
            }

            case DeploymentSource.Image:
                await context.ReportProgressAsync(15, "Pulling image", ct).ConfigureAwait(false);
                return await runtime.Images
                    .PullAsync(ImageReference.Parse(app.ImageRef!), progress, ct)
                    .ConfigureAwait(false);

            case DeploymentSource.Dockerfile:
            {
                await context.ReportProgressAsync(15, "Building image", ct).ConfigureAwait(false);
                using var workspace = Workspace.Create();
                await File.WriteAllTextAsync(
                    Path.Combine(workspace.Path, "Dockerfile"), app.DockerfileContent!, ct).ConfigureAwait(false);

                return await BuildAsync(app, workspace.Path, "Dockerfile", deploymentId, labels, progress, ct)
                    .ConfigureAwait(false);
            }

            case DeploymentSource.Git:
            {
                await context.ReportProgressAsync(10, "Cloning repository", ct).ConfigureAwait(false);
                using var workspace = Workspace.Create();

                var checkout = await git
                    .CloneAsync(app.GitRepositoryUrl!, app.GitBranch, workspace.Path, ct)
                    .ConfigureAwait(false);

                await context.LogStepAsync(
                    "clone", $"Checked out {checkout.CommitSha[..Math.Min(8, checkout.CommitSha.Length)]}", ct)
                    .ConfigureAwait(false);

                var dockerfile = BuildContextPaths.ResolveWithin(workspace.Path, app.DockerfilePath ?? "Dockerfile");

                if (dockerfile.IsFailure)
                {
                    throw new InvalidOperationException(dockerfile.Failure!.Message);
                }

                if (!File.Exists(dockerfile.Value))
                {
                    throw new InvalidOperationException(
                        $"No Dockerfile at '{app.DockerfilePath ?? "Dockerfile"}' in the repository. "
                        + "Airside builds from a Dockerfile and does not detect frameworks.");
                }

                await context.ReportProgressAsync(25, "Building image", ct).ConfigureAwait(false);

                return await BuildAsync(
                    app, workspace.Path, app.DockerfilePath ?? "Dockerfile", deploymentId, labels, progress, ct)
                    .ConfigureAwait(false);
            }

            default:
                throw new InvalidOperationException($"Unsupported deployment source {app.Source}.");
        }
    }

    private Task<ImageSummary> BuildAsync(
        ApplicationSnapshot app,
        string contextPath,
        string dockerfilePath,
        Guid deploymentId,
        IReadOnlyDictionary<string, string> labels,
        IProgress<string> progress,
        CancellationToken ct) =>
        runtime.Images.BuildAsync(
            new ImageBuildRequest(
                contextPath,
                dockerfilePath,
                // Tagged with the deployment, so two builds of the same
                // application never overwrite each other's image and a rollback
                // has something to point at.
                new ImageReference($"airside/{app.Slug.Value}", AirsideNames.ShortId(deploymentId)),
                labels),
            progress,
            ct);

    private async Task<string> CreateContainerAsync(
        IJobContext context,
        ApplicationSnapshot app,
        ImageSummary image,
        string containerName,
        CancellationToken ct)
    {
        var existing = await runtime.Containers.FindAsync(containerName, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            await context.TrackResourceAsync(JobResourceKind.Container, existing.Id, true, ct).ConfigureAwait(false);
            return existing.Id;
        }

        var spec = new ContainerSpec
        {
            Name = containerName,
            Image = ImageReference.Parse(image.Digest),
            Labels = Labels(app),
            Limits = new ContainerLimits(app.MemoryBytes, app.CpuNanos),
            NetworkName = app.NetworkName,
            RestartPolicy = app.AutoRestart ? RestartPolicy.UnlessStopped : RestartPolicy.No,
            Environment = app.Environment,
            HealthProbe = app.HealthProbe,

            // An application image is not Airside's, so it gets the strict
            // profile: no capabilities restored, no privilege escalation.
            Security = ContainerSecurity.Default,
        };

        var containerId = await runtime.Containers.CreateAsync(spec, ct).ConfigureAwait(false);
        await context.TrackResourceAsync(JobResourceKind.Container, containerId, true, ct).ConfigureAwait(false);

        return containerId;
    }

    private async Task<bool> WaitForHealthAsync(IJobContext context, string containerId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(HealthTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var container = await runtime.Containers.FindAsync(containerId, ct).ConfigureAwait(false);

            if (container is null)
            {
                return false;
            }

            switch (container.Health)
            {
                case ContainerHealth.Healthy:
                    await context.LogStepAsync("health", "Health check passed.", ct).ConfigureAwait(false);
                    return true;

                case ContainerHealth.Unhealthy when container.State is ContainerRunState.Exited:
                case ContainerHealth.None when container.State is ContainerRunState.Exited:
                    await context.LogStepAsync(
                        "health", $"Container exited with code {container.ExitCode}.", ct).ConfigureAwait(false);
                    return false;

                default:
                    break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        await context.LogStepAsync("health", "Timed out waiting for the health check.", ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Removes the new container and leaves the running version alone.
    /// </summary>
    /// <remarks>
    /// The previous deployment is deliberately not touched here. It was never
    /// stopped unless the new one passed its health check, so the correct recovery
    /// from a failed deploy is to remove what this job made and change nothing
    /// else — the application keeps serving from the version it was already on.
    /// </remarks>
    public async Task CompensateAsync(IJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var resource in (await context.GetTrackedResourcesAsync(ct).ConfigureAwait(false)).Reverse())
        {
            if (!resource.CreatedByThisJob)
            {
                continue;
            }

            try
            {
                switch (resource.Kind)
                {
                    case JobResourceKind.Container:
                        await runtime.Containers.RemoveAsync(resource.Reference, force: true, ct)
                            .ConfigureAwait(false);
                        await context.LogStepAsync("compensate", "Removed the failed container.", ct)
                            .ConfigureAwait(false);
                        break;

                    case JobResourceKind.Network:
                        // Only when this job created it. A redeploy that fails must
                        // leave the network the running version is attached to.
                        await runtime.Networks.RemoveAsync(resource.Reference, ct).ConfigureAwait(false);
                        await context.LogStepAsync("compensate", "Removed the network it created.", ct)
                            .ConfigureAwait(false);
                        break;

                    default:
                        break;
                }
            }
            catch (ContainerRuntimeException ex)
            {
                logger.LogError(ex, "Could not remove {Kind} {Reference}", resource.Kind, resource.Reference);
            }
        }

        var payload = context.GetPayload<DeployPayload>();

        // A deployment whose job died is not still building. Without this the row
        // sits in Building for ever and the history reads as an in-flight deploy
        // that nothing will ever finish.
        await store.RecordDeploymentFailedAsync(
            payload.DeploymentId,
            "deployment.failed",
            "The deployment job did not complete. The previously running version was not affected.",
            ct).ConfigureAwait(false);

        var app = await store.GetAsync(payload.WorkloadId, payload.DeploymentId, ct).ConfigureAwait(false);

        // Back to Running if a previous version is still up, Failed only if there
        // is nothing serving. A first deployment that fails leaves nothing behind.
        await store.SetStateAsync(
            payload.WorkloadId,
            app?.CurrentContainerId is not null
                ? ApplicationState.Running.ToString()
                : ApplicationState.Failed.ToString(),
            ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Labels(ApplicationSnapshot app) => new(StringComparer.Ordinal)
    {
        [AirsideLabels.Managed] = AirsideLabels.True,
        [AirsideLabels.Kind] = AirsideLabels.KindApplication,
        [AirsideLabels.WorkloadId] = app.Id.ToString(),
        [AirsideLabels.Slug] = app.Slug.Value,
    };
}

/// <summary>A temporary directory that removes itself.</summary>
internal sealed class Workspace : IDisposable
{
    private Workspace(string path) => Path = path;

    public string Path { get; }

    public static Workspace Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"airside-build-{Guid.CreateVersion7():N}");

        Directory.CreateDirectory(path);
        return new Workspace(path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover build directory is untidy, not dangerous.
        }
    }
}

/// <summary>Raised when a rollback target's image is no longer on the host.</summary>
public sealed class DeploymentImageMissingException : Exception
{
    public DeploymentImageMissingException()
    {
    }

    public DeploymentImageMissingException(string message)
        : base(message)
    {
    }

    public DeploymentImageMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
