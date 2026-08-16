using System.Formats.Tar;
using Airside.Core.Containers;
using Airside.Core.Naming;
using DD = Docker.DotNet;
using DM = Docker.DotNet.Models;

namespace Airside.Runtime.Docker;

internal sealed class DockerImageOperations(DD.IDockerClient client) : IImageOperations
{
    public async Task<ImageSummary> PullAsync(
        ImageReference image,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);

        try
        {
            await client.Images.CreateImageAsync(
                new DM.ImagesCreateParameters { FromImage = image.Repository, Tag = image.Digest ?? image.Tag },
                authConfig: null,
                new Progress<DM.JSONMessage>(m => progress?.Report(m.Status ?? m.ProgressMessage ?? string.Empty)),
                ct).ConfigureAwait(false);

            return await FindAsync(image, ct).ConfigureAwait(false)
                ?? throw new ContainerRuntimeException($"Image {image} was pulled but cannot be inspected.");
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not pull image {image}.", ex);
        }
    }

    public async Task<ImageSummary> BuildAsync(
        ImageBuildRequest request,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The build context is streamed as a tar archive built from the cloned
        // repository. The path came from a validated application record, never
        // straight from a request body.
        using var context = new MemoryStream();
        await TarFile.CreateFromDirectoryAsync(request.ContextPath, context, includeBaseDirectory: false, ct)
            .ConfigureAwait(false);
        context.Position = 0;

        try
        {
            await client.Images.BuildImageFromDockerfileAsync(
                new DM.ImageBuildParameters
                {
                    Dockerfile = request.DockerfilePath,
                    Tags = [request.Tag.ToString()],
                    Labels = new Dictionary<string, string>(request.Labels, StringComparer.Ordinal),
                    BuildArgs = request.BuildArguments?.ToDictionary(
                        a => a.Key,
                        a => a.Value.Reveal(),
                        StringComparer.Ordinal),
                },
                context,
                authConfigs: null,
                headers: null,
                new Progress<DM.JSONMessage>(m => progress?.Report(m.Stream ?? m.Status ?? string.Empty)),
                ct).ConfigureAwait(false);

            return await FindAsync(request.Tag, ct).ConfigureAwait(false)
                ?? throw new ContainerRuntimeException($"Image {request.Tag} was built but cannot be inspected.");
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not build image {request.Tag}.", ex);
        }
    }

    public async Task<ImageSummary?> FindAsync(ImageReference image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);

        try
        {
            var inspect = await client.Images.InspectImageAsync(image.ToString(), ct).ConfigureAwait(false);
            return DockerMapping.ToSummary(inspect);
        }
        catch (DD.DockerImageNotFoundException)
        {
            return null;
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not inspect image {image}.", ex);
        }
    }

    public async Task RemoveAsync(string imageId, bool force, CancellationToken ct)
    {
        try
        {
            await client.Images
                .DeleteImageAsync(imageId, new DM.ImageDeleteParameters { Force = force }, ct)
                .ConfigureAwait(false);
        }
        catch (DD.DockerImageNotFoundException)
        {
            // Idempotent, as with container removal.
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not remove image {imageId}.", ex);
        }
    }
}

internal sealed class DockerVolumeOperations(DD.IDockerClient client) : IVolumeOperations
{
    public async Task<VolumeSummary> CreateAsync(VolumeSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        try
        {
            var created = await client.Volumes.CreateAsync(
                new DM.VolumesCreateParameters
                {
                    Name = spec.Name,
                    Driver = "local",
                    Labels = new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal),
                },
                ct).ConfigureAwait(false);

            return DockerMapping.ToSummary(created);
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not create volume {spec.Name}.", ex);
        }
    }

    public async Task<VolumeSummary?> FindAsync(string name, CancellationToken ct)
    {
        try
        {
            return DockerMapping.ToSummary(await client.Volumes.InspectAsync(name, ct).ConfigureAwait(false));
        }
        catch (DD.DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not inspect volume {name}.", ex);
        }
    }

    public async Task<IReadOnlyList<VolumeSummary>> ListManagedAsync(CancellationToken ct)
    {
        try
        {
            var listed = await client.Volumes
                .ListAsync(new DM.VolumesListParameters { Filters = DockerMapping.ManagedFilter(null) }, ct)
                .ConfigureAwait(false);

            return [.. (listed.Volumes ?? []).Select(DockerMapping.ToSummary)];
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException("Could not list managed volumes.", ex);
        }
    }

    public async Task RemoveAsync(string name, bool force, CancellationToken ct)
    {
        try
        {
            await client.Volumes.RemoveAsync(name, force, ct).ConfigureAwait(false);
        }
        catch (DD.DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotent.
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not remove volume {name}.", ex);
        }
    }

    /// <summary>
    /// Measures a volume by running <c>du</c> in a throwaway container that mounts
    /// it read-only.
    /// </summary>
    /// <remarks>
    /// Docker exposes no per-volume size API, and the API container cannot see the
    /// host's volume directory. The helper is a fixed image with a fixed argument
    /// vector; nothing about it derives from user input except the volume name,
    /// which is a derived name built from a validated slug.
    /// </remarks>
    public async Task<long> MeasureAsync(string name, CancellationToken ct)
    {
        const string mountPoint = "/measured";

        var created = await client.Containers.CreateContainerAsync(
            new DM.CreateContainerParameters
            {
                Image = MeasurementImage,
                Cmd = ["du", "-sb", mountPoint],
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AirsideLabels.Managed] = AirsideLabels.True,
                    [AirsideLabels.Kind] = AirsideLabels.KindSystem,
                },
                HostConfig = new DM.HostConfig
                {
                    AutoRemove = true,
                    Mounts =
                    [
                        new DM.Mount
                        {
                            Type = "volume",
                            Source = name,
                            Target = mountPoint,
                            ReadOnly = true,
                        },
                    ],
                    // Measurement must never be able to change what it measures.
                    ReadonlyRootfs = true,
                    SecurityOpt = ["no-new-privileges:true"],
                    CapDrop = ["ALL"],
                },
            },
            ct).ConfigureAwait(false);

        using var output = new MemoryStream();

        await client.Containers
            .StartContainerAsync(created.ID, new DM.ContainerStartParameters(), ct)
            .ConfigureAwait(false);

        await client.Containers
            .WaitContainerAsync(created.ID, ct)
            .ConfigureAwait(false);

        using var logs = await client.Containers.GetContainerLogsAsync(
            created.ID,
            tty: false,
            new DM.ContainerLogsParameters { ShowStdout = true },
            ct).ConfigureAwait(false);

        await logs.CopyOutputToAsync(Stream.Null, output, Stream.Null, ct).ConfigureAwait(false);

        var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
        var firstField = text.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return long.TryParse(firstField?.Trim(), out var bytes) ? bytes : 0;
    }

    /// <summary>Pinned by digest so a measurement helper cannot silently change under us.</summary>
    private const string MeasurementImage = "busybox:1.36";

    /// <summary>
    /// Streams a file out of a volume as a tar archive entry.
    /// </summary>
    /// <remarks>
    /// Docker has no volume-level file API, so a throwaway container mounts the
    /// volume read-only and the archive is pulled from it. The helper image is
    /// fixed and its command is a fixed argument vector; the only value derived
    /// from anything user-supplied is the volume name, which is built from a
    /// validated slug.
    /// </remarks>
    public async Task CopyFromAsync(
        string volumeName,
        string pathInVolume,
        Stream destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var containerId = await StartHelperAsync(volumeName, readOnly: true, ct).ConfigureAwait(false);

        try
        {
            var response = await client.Containers.GetArchiveFromContainerAsync(
                containerId,
                new DM.GetArchiveFromContainerParameters { Path = $"{HelperMount}/{pathInVolume}" },
                statOnly: false,
                ct).ConfigureAwait(false);

            // Spooled to a temp file before parsing. Docker.DotNet hands back a
            // chunked HTTP stream, and TarReader reads each entry for exactly its
            // declared length — against that stream the read runs off the end and
            // throws EndOfStreamException partway through the payload. A seekable
            // file makes the framing reliable, and a temp file rather than memory
            // because a data volume can be far larger than the control plane's
            // heap.
            var spool = Path.Combine(Path.GetTempPath(), $"airside-archive-{Guid.CreateVersion7():N}.tar");

            try
            {
                await using (var spooled = File.Create(spool))
                {
                    using var responseStream = response.Stream;
                    await responseStream.CopyToAsync(spooled, ct).ConfigureAwait(false);
                }

                await using var archive = File.OpenRead(spool);
                await ExtractSingleEntryAsync(archive, destination, ct).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(spool))
                {
                    File.Delete(spool);
                }
            }
        }
        finally
        {
            await client.Containers
                .RemoveContainerAsync(containerId, new DM.ContainerRemoveParameters { Force = true }, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a file into a volume.
    /// </summary>
    /// <remarks>
    /// Only valid when nothing has the volume open for writing. The Redis restore
    /// flow stops the container first, which is why
    /// <c>RequiresStopForRestore</c> exists as a capability rather than as a
    /// comment.
    /// </remarks>
    public async Task CopyIntoAsync(
        string volumeName,
        string pathInVolume,
        Stream source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        var containerId = await StartHelperAsync(volumeName, readOnly: false, ct).ConfigureAwait(false);

        try
        {
            using var archive = new MemoryStream();
            await WriteSingleEntryTarAsync(archive, pathInVolume, source, ct).ConfigureAwait(false);
            archive.Position = 0;

            await client.Containers.ExtractArchiveToContainerAsync(
                containerId,
                new DM.ContainerPathStatParameters { Path = HelperMount, AllowOverwriteDirWithFile = false },
                archive,
                ct).ConfigureAwait(false);
        }
        finally
        {
            await client.Containers
                .RemoveContainerAsync(containerId, new DM.ContainerRemoveParameters { Force = true }, ct)
                .ConfigureAwait(false);
        }
    }

    private const string HelperMount = "/airside-volume";

    private async Task<string> StartHelperAsync(string volumeName, bool readOnly, CancellationToken ct)
    {
        var created = await client.Containers.CreateContainerAsync(
            new DM.CreateContainerParameters
            {
                Image = MeasurementImage,
                // It has to run. Docker mounts a volume when the container
                // starts, not when it is created, so an archive pulled from a
                // never-started helper is empty — which surfaces as "attempted to
                // read past the end of the stream" rather than anything that names
                // the cause. `sleep` keeps it alive with no network, no
                // capabilities, and a read-only root while the copy happens.
                Cmd = ["sleep", "3600"],
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AirsideLabels.Managed] = AirsideLabels.True,
                    [AirsideLabels.Kind] = AirsideLabels.KindSystem,
                },
                HostConfig = new DM.HostConfig
                {
                    Mounts =
                    [
                        new DM.Mount
                        {
                            Type = "volume",
                            Source = volumeName,
                            Target = HelperMount,
                            ReadOnly = readOnly,
                        },
                    ],
                    ReadonlyRootfs = true,
                    SecurityOpt = ["no-new-privileges:true"],
                    CapDrop = ["ALL"],
                    NetworkMode = "none",
                },
            },
            ct).ConfigureAwait(false);

        await client.Containers
            .StartContainerAsync(created.ID, new DM.ContainerStartParameters(), ct)
            .ConfigureAwait(false);

        return created.ID;
    }

    /// <summary>
    /// Reads the first regular file out of a tar stream.
    /// </summary>
    /// <remarks>
    /// The entry length is honoured exactly. Copying the whole stream would append
    /// tar's trailing padding and end-of-archive blocks to the payload, which for
    /// an RDB file means a backup that is silently a few hundred bytes longer than
    /// the data and fails to load.
    /// </remarks>
    private static async Task ExtractSingleEntryAsync(Stream archive, Stream destination, CancellationToken ct)
    {
        using var reader = new TarReader(archive, leaveOpen: true);

        while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.DataStream is null)
            {
                continue;
            }

            await entry.DataStream.CopyToAsync(destination, ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        throw new ContainerRuntimeException("The volume contained no such file.");
    }

    private static async Task WriteSingleEntryTarAsync(
        Stream archive,
        string entryName,
        Stream content,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        await using var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true);

        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = buffer,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        };

        await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
    }
}

internal sealed class DockerNetworkOperations(DD.IDockerClient client) : INetworkOperations
{
    public async Task<NetworkSummary> CreateAsync(NetworkSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        try
        {
            var created = await client.Networks.CreateNetworkAsync(
                new DM.NetworksCreateParameters
                {
                    Name = spec.Name,
                    Driver = "bridge",
                    Internal = spec.Internal,
                    Labels = new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal),
                },
                ct).ConfigureAwait(false);

            return await FindAsync(spec.Name, ct).ConfigureAwait(false)
                ?? new NetworkSummary(created.ID, spec.Name, spec.Labels, []);
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException(
                $"Could not create network {spec.Name}. If this reports no available address pool, "
                + "the Docker daemon needs default-address-pools configured — see deploy/daemon.json.",
                ex);
        }
    }

    public async Task<NetworkSummary?> FindAsync(string name, CancellationToken ct)
    {
        try
        {
            return DockerMapping.ToSummary(
                await client.Networks.InspectNetworkAsync(name, ct).ConfigureAwait(false));
        }
        catch (DD.DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not inspect network {name}.", ex);
        }
    }

    public async Task<IReadOnlyList<NetworkSummary>> ListManagedAsync(CancellationToken ct)
    {
        try
        {
            var listed = await client.Networks
                .ListNetworksAsync(new DM.NetworksListParameters { Filters = DockerMapping.ManagedFilter(null) }, ct)
                .ConfigureAwait(false);

            return [.. listed.Select(DockerMapping.ToSummary)];
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException("Could not list managed networks.", ex);
        }
    }

    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        try
        {
            await client.Networks.DeleteNetworkAsync(name, ct).ConfigureAwait(false);
        }
        catch (DD.DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotent.
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not remove network {name}.", ex);
        }
    }

    public async Task ConnectAsync(string networkName, string containerId, CancellationToken ct)
    {
        try
        {
            await client.Networks.ConnectNetworkAsync(
                networkName,
                new DM.NetworkConnectParameters { Container = containerId },
                ct).ConfigureAwait(false);
        }
        catch (DD.DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // Already attached. Attach is the enforcement half of a database
            // attachment and must be safe to re-run during job recovery.
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException(
                $"Could not connect {containerId} to network {networkName}.", ex);
        }
    }

    public async Task DisconnectAsync(string networkName, string containerId, CancellationToken ct)
    {
        try
        {
            await client.Networks.DisconnectNetworkAsync(
                networkName,
                new DM.NetworkDisconnectParameters { Container = containerId, Force = false },
                ct).ConfigureAwait(false);
        }
        catch (DD.DockerApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                                            or System.Net.HttpStatusCode.Forbidden)
        {
            // Already detached.
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException(
                $"Could not disconnect {containerId} from network {networkName}.", ex);
        }
    }
}
