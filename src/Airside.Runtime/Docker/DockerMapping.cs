using System.Globalization;
using Airside.Core.Containers;
using Airside.Core.Naming;
using DM = Docker.DotNet.Models;

namespace Airside.Runtime.Docker;

/// <summary>
/// Translates between Airside types and Docker.DotNet types.
/// </summary>
/// <remarks>
/// <para>
/// This file is the boundary. No Docker.DotNet type crosses out of it into the
/// rest of the system, which is what keeps a Podman or remote-agent
/// implementation a matter of writing a sibling rather than editing callers.
/// </para>
/// <para>
/// Docker types are reached through the <c>DM</c> alias rather than an imported
/// namespace. Several of them collide with Airside's own names — Docker has its
/// own <c>ContainerSpec</c>, <c>PortBinding</c>, and <c>RestartPolicy</c> — and
/// the prefix makes it obvious at a glance which side of the boundary a type
/// belongs to.
/// </para>
/// </remarks>
internal static class DockerMapping
{
    public static DM.CreateContainerParameters ToCreateParameters(ContainerSpec spec)
    {
        var hostConfig = new DM.HostConfig
        {
            Memory = spec.Limits.MemoryBytes,
            NanoCPUs = spec.Limits.CpuNanos,
            RestartPolicy = ToRestartPolicy(spec.RestartPolicy),
            Mounts = [.. spec.Mounts.Select(ToMount)],
            PortBindings = ToPortBindings(spec.Ports),

            // Hardening applied to everything Airside creates.
            SecurityOpt = spec.Security.NoNewPrivileges ? ["no-new-privileges:true"] : [],
            CapDrop = [.. spec.Security.DropCapabilities],
            CapAdd = [.. spec.Security.AddCapabilities],
            ReadonlyRootfs = spec.Security.ReadOnlyRootFilesystem,
        };

        return new DM.CreateContainerParameters
        {
            Name = spec.Name,
            Image = spec.Image.ToString(),
            Cmd = spec.Command is null ? null : [.. spec.Command],
            Env = [.. spec.Environment.Select(e => $"{e.Key}={e.Value.Reveal()}")],
            Labels = new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal),
            User = spec.Security.User,
            HostConfig = hostConfig,
            ExposedPorts = spec.Ports.ToDictionary(PortKey, _ => default(DM.EmptyStruct), StringComparer.Ordinal),
            Healthcheck = ToHealthConfig(spec.HealthProbe),
            NetworkingConfig = spec.NetworkName is null
                ? null
                : new DM.NetworkingConfig
                {
                    EndpointsConfig = new Dictionary<string, DM.EndpointSettings>(StringComparer.Ordinal)
                    {
                        [spec.NetworkName] = new DM.EndpointSettings(),
                    },
                },
        };
    }

    /// <summary>
    /// Only named volumes. There is no host-path branch here, because
    /// <see cref="VolumeMount"/> has no host-path variant — an arbitrary bind
    /// mount is inexpressible rather than merely rejected.
    /// </summary>
    private static DM.Mount ToMount(VolumeMount mount) => new()
    {
        Type = "volume",
        Source = mount.VolumeName,
        Target = mount.ContainerPath,
        ReadOnly = mount.ReadOnly,
    };

    private static string PortKey(PortBinding binding) =>
        $"{binding.ContainerPort}/{(binding.Protocol == PortProtocol.Udp ? "udp" : "tcp")}";

    private static Dictionary<string, IList<DM.PortBinding>> ToPortBindings(IReadOnlyList<PortBinding> ports)
    {
        var result = new Dictionary<string, IList<DM.PortBinding>>(StringComparer.Ordinal);

        foreach (var port in ports)
        {
            result[PortKey(port)] =
            [
                new DM.PortBinding
                {
                    // Loopback unless the admin explicitly opted into public
                    // exposure. A default of 0.0.0.0 would put databases on the
                    // internet within a week of launch.
                    HostIP = port.BindAddress,
                    HostPort = port.HostPort.ToString(CultureInfo.InvariantCulture),
                },
            ];
        }

        return result;
    }

    private static DM.RestartPolicy ToRestartPolicy(RestartPolicy policy) => new()
    {
        Name = policy switch
        {
            RestartPolicy.No => DM.RestartPolicyKind.No,
            RestartPolicy.OnFailure => DM.RestartPolicyKind.OnFailure,
            RestartPolicy.UnlessStopped => DM.RestartPolicyKind.UnlessStopped,
            RestartPolicy.Always => DM.RestartPolicyKind.Always,
            _ => DM.RestartPolicyKind.UnlessStopped,
        },
    };

    private static DM.HealthConfig? ToHealthConfig(HealthProbe? probe) => probe is null
        ? null
        : new DM.HealthConfig
        {
            // CMD, not CMD-SHELL. There is no shell anywhere in Airside.
            Test = ["CMD", .. probe.Command],
            Interval = probe.Interval,
            Timeout = probe.Timeout,
            Retries = probe.Retries,
            StartPeriod = (long)probe.StartPeriod.TotalMilliseconds * 1_000_000,
        };

    public static ContainerSummary ToSummary(DM.ContainerInspectResponse inspect)
    {
        var labels = inspect.Config?.Labels is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(inspect.Config.Labels, StringComparer.Ordinal);

        return new ContainerSummary(
            Id: inspect.ID,
            Name: inspect.Name?.TrimStart('/') ?? string.Empty,
            Image: inspect.Config?.Image ?? string.Empty,
            ImageDigest: inspect.Image,
            State: ToRunState(inspect.State?.Status),
            CreatedAt: inspect.Created,
            StartedAt: ParseOptionalTime(inspect.State?.StartedAt),
            ExitCode: inspect.State is null ? null : (int)inspect.State.ExitCode,
            Health: ToHealth(inspect.State?.Health),
            Labels: labels,
            Networks: inspect.NetworkSettings?.Networks?.Keys.ToList() ?? []);
    }

    public static ContainerSummary ToSummary(DM.ContainerListResponse listed) => new(
        Id: listed.ID,
        Name: listed.Names?.FirstOrDefault()?.TrimStart('/') ?? string.Empty,
        Image: listed.Image,
        ImageDigest: listed.ImageID,
        State: ToRunState(listed.State),
        CreatedAt: listed.Created,
        StartedAt: null,
        ExitCode: null,
        Health: ContainerHealth.None,
        Labels: listed.Labels is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(listed.Labels, StringComparer.Ordinal),
        Networks: listed.NetworkSettings?.Networks?.Keys.ToList() ?? []);

    public static VolumeSummary ToSummary(DM.VolumeResponse volume) => new(
        Name: volume.Name,
        MountPoint: volume.Mountpoint,
        CreatedAt: ParseOptionalTime(volume.CreatedAt) ?? default,
        Labels: volume.Labels is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(volume.Labels, StringComparer.Ordinal));

    public static NetworkSummary ToSummary(DM.NetworkResponse network) => new(
        Id: network.ID,
        Name: network.Name,
        Labels: network.Labels is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(network.Labels, StringComparer.Ordinal),
        ConnectedContainerIds: network.Containers?.Keys.ToList() ?? []);

    public static ImageSummary ToSummary(DM.ImageInspectResponse image) => new(
        Id: image.ID,
        Digest: image.RepoDigests?.FirstOrDefault() ?? image.ID,
        Tags: image.RepoTags?.ToList() ?? [],
        SizeBytes: image.Size,
        CreatedAt: image.Created);

    private static DateTimeOffset? ParseOptionalTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed.Year > 1
            ? parsed
            : null;

    private static ContainerRunState ToRunState(string? status) => status switch
    {
        "created" => ContainerRunState.Created,
        "running" => ContainerRunState.Running,
        "paused" => ContainerRunState.Paused,
        "restarting" => ContainerRunState.Restarting,
        "exited" => ContainerRunState.Exited,
        "dead" => ContainerRunState.Dead,
        _ => ContainerRunState.Created,
    };

    private static ContainerHealth ToHealth(DM.Health? health) => health?.Status switch
    {
        "starting" => ContainerHealth.Starting,
        "healthy" => ContainerHealth.Healthy,
        "unhealthy" => ContainerHealth.Unhealthy,
        _ => ContainerHealth.None,
    };

    /// <summary>The label filter every managed lookup starts from.</summary>
    public static Dictionary<string, IDictionary<string, bool>> ManagedFilter(
        IReadOnlyDictionary<string, string>? extra)
    {
        var labels = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [$"{AirsideLabels.Managed}={AirsideLabels.True}"] = true,
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                labels[$"{key}={value}"] = true;
            }
        }

        return new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["label"] = labels,
        };
    }
}
