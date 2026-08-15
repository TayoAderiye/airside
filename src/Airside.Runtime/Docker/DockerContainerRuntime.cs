using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Airside.Core.Containers;
using DD = Docker.DotNet;
using Microsoft.Extensions.Logging;
using DM = Docker.DotNet.Models;

namespace Airside.Runtime.Docker;

/// <summary>Docker-backed <see cref="IContainerRuntime"/>.</summary>
public sealed class DockerContainerRuntime : IContainerRuntime, IDisposable
{
    private readonly DD.IDockerClient _client;
    private readonly bool _ownsClient;

    public DockerContainerRuntime(DD.IDockerClient client, ILoggerFactory loggerFactory, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _client = client;
        _ownsClient = ownsClient;

        Containers = new DockerContainerOperations(client, loggerFactory.CreateLogger<DockerContainerOperations>());
        Images = new DockerImageOperations(client);
        Volumes = new DockerVolumeOperations(client);
        Networks = new DockerNetworkOperations(client);
    }

    public IContainerOperations Containers { get; }

    public IImageOperations Images { get; }

    public IVolumeOperations Volumes { get; }

    public INetworkOperations Networks { get; }

    public async Task<RuntimeInfo> GetInfoAsync(CancellationToken ct)
    {
        try
        {
            var version = await _client.System.GetVersionAsync(ct).ConfigureAwait(false);
            var info = await _client.System.GetSystemInfoAsync(ct).ConfigureAwait(false);

            return new RuntimeInfo(
                ApiVersion: version.APIVersion,
                ServerVersion: version.Version,
                OperatingSystem: info.OperatingSystem ?? version.Os,
                KernelVersion: info.KernelVersion ?? version.KernelVersion,
                TotalCpuCount: (int)info.NCPU,
                TotalMemoryBytes: info.MemTotal);
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException("Could not read Docker runtime information.", ex);
        }
    }

    /// <summary>Never throws — the health endpoint calls this and must report, not fail.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            await _client.System.PingAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DD.DockerApiException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}

internal sealed class DockerContainerOperations(DD.IDockerClient client, ILogger<DockerContainerOperations> logger)
    : IContainerOperations
{
    /// <summary>
    /// Last CPU reading per container. Docker's one-shot stats call reports
    /// <c>precpu_stats</c> as zeros, so a single sample cannot yield a CPU figure
    /// — the first call for a container returns null rather than a plausible 0%.
    /// </summary>
    private readonly ConcurrentDictionary<string, (ulong TotalUsage, DateTimeOffset At)> _cpuBaseline = new();

    public async Task<string> CreateAsync(ContainerSpec spec, CancellationToken ct)
    {
        try
        {
            var response = await client.Containers
                .CreateContainerAsync(DockerMapping.ToCreateParameters(spec), ct)
                .ConfigureAwait(false);

            foreach (var warning in response.Warnings ?? [])
            {
                logger.LogWarning("Docker warned while creating {ContainerName}: {Warning}", spec.Name, warning);
            }

            return response.ID;
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not create container {spec.Name}.", ex);
        }
    }

    public async Task StartAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await client.Containers
                .StartContainerAsync(containerId, new DM.ContainerStartParameters(), ct)
                .ConfigureAwait(false);
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not start container {containerId}.", ex);
        }
    }

    public async Task StopAsync(string containerId, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await client.Containers.StopContainerAsync(
                containerId,
                new DM.ContainerStopParameters { WaitBeforeKillSeconds = (uint)timeout.TotalSeconds },
                ct).ConfigureAwait(false);
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not stop container {containerId}.", ex);
        }
    }

    public async Task RestartAsync(string containerId, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await client.Containers.RestartContainerAsync(
                containerId,
                new DM.ContainerRestartParameters { WaitBeforeKillSeconds = (uint)timeout.TotalSeconds },
                ct).ConfigureAwait(false);
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not restart container {containerId}.", ex);
        }
    }

    public async Task RemoveAsync(string containerId, bool force, CancellationToken ct)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(
                containerId,
                // RemoveVolumes stays false unconditionally. Deleting a database
                // must not delete its data unless the admin explicitly opted in,
                // and that decision is made far above this layer — the runtime is
                // not the place to be persuaded.
                new DM.ContainerRemoveParameters { Force = force, RemoveVolumes = false },
                ct).ConfigureAwait(false);
        }
        catch (DD.DockerContainerNotFoundException)
        {
            // Removal is idempotent: a compensating cleanup re-run must not fail
            // because the previous attempt already succeeded.
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not remove container {containerId}.", ex);
        }
    }

    public async Task<ContainerSummary?> FindAsync(string idOrName, CancellationToken ct)
    {
        try
        {
            var inspect = await client.Containers.InspectContainerAsync(idOrName, ct).ConfigureAwait(false);
            return DockerMapping.ToSummary(inspect);
        }
        catch (DD.DockerContainerNotFoundException)
        {
            return null;
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not inspect container {idOrName}.", ex);
        }
    }

    public async Task<IReadOnlyList<ContainerSummary>> ListManagedAsync(
        IReadOnlyDictionary<string, string>? labelFilters,
        CancellationToken ct)
    {
        try
        {
            var listed = await client.Containers.ListContainersAsync(
                new DM.ContainersListParameters
                {
                    All = true,
                    Filters = DockerMapping.ManagedFilter(labelFilters),
                },
                ct).ConfigureAwait(false);

            return [.. listed.Select(DockerMapping.ToSummary)];
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException("Could not list managed containers.", ex);
        }
    }

    public async IAsyncEnumerable<ContainerLogLine> StreamLogsAsync(
        string containerId,
        LogQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var stream = await client.Containers.GetContainerLogsAsync(
            containerId,
            tty: false,
            new DM.ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = query.IncludeStandardError,
                Follow = query.Follow,
                Timestamps = true,
                Tail = query.TailLines?.ToString(CultureInfo.InvariantCulture) ?? "all",
                Since = query.Since?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            },
            ct).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        var pending = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);

            if (read.EOF)
            {
                break;
            }

            var source = read.Target == DD.MultiplexedStream.TargetStream.StandardError
                ? LogSource.StandardError
                : LogSource.StandardOutput;

            pending.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));

            foreach (var line in DrainCompleteLines(pending))
            {
                yield return ParseLine(line, source);
            }
        }

        if (pending.Length > 0)
        {
            yield return ParseLine(pending.ToString(), LogSource.StandardOutput);
        }
    }

    /// <summary>
    /// Yields only whole lines, leaving any partial trailing line in the buffer.
    /// A 16 KiB read routinely lands mid-line, and emitting the fragment produces
    /// a log view that splits messages at arbitrary points.
    /// </summary>
    private static List<string> DrainCompleteLines(StringBuilder pending)
    {
        var text = pending.ToString();
        var lines = text.Split('\n');
        pending.Clear();
        pending.Append(lines[^1]);
        return [.. lines[..^1].Where(l => l.Length > 0)];
    }

    /// <summary>
    /// Docker prefixes each line with an RFC 3339 timestamp when Timestamps is
    /// set. A line whose prefix does not parse is emitted whole rather than
    /// discarded — losing a log line to a parsing quirk is worse than an
    /// approximate timestamp.
    /// </summary>
    private static ContainerLogLine ParseLine(string raw, LogSource source)
    {
        var separator = raw.IndexOf(' ', StringComparison.Ordinal);

        if (separator > 0
            && DateTimeOffset.TryParse(
                raw[..separator],
                CultureInfo.InvariantCulture,
                out var timestamp))
        {
            return new ContainerLogLine(timestamp, source, raw[(separator + 1)..].TrimEnd('\r'));
        }

        return new ContainerLogLine(DateTimeOffset.UnixEpoch, source, raw.TrimEnd('\r'));
    }

    public async Task<ContainerStatsSample?> SampleStatsAsync(string containerId, CancellationToken ct)
    {
        DM.ContainerStatsResponse? captured = null;
        var sink = new Progress<DM.ContainerStatsResponse>(r => captured = r);

        try
        {
            await client.Containers.GetContainerStatsAsync(
                containerId,
                new DM.ContainerStatsParameters { Stream = false },
                sink,
                ct).ConfigureAwait(false);
        }
        catch (DD.DockerContainerNotFoundException)
        {
            _cpuBaseline.TryRemove(containerId, out _);
            return null;
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Could not sample stats for {containerId}.", ex);
        }

        if (captured is null)
        {
            return null;
        }

        var now = captured.Read == default ? DateTimeOffset.UtcNow : new DateTimeOffset(captured.Read, TimeSpan.Zero);
        var total = captured.CPUStats?.CPUUsage?.TotalUsage ?? 0;

        long? cpuNanos = null;

        if (_cpuBaseline.TryGetValue(containerId, out var previous))
        {
            var elapsed = (now - previous.At).TotalSeconds;

            if (elapsed > 0 && total >= previous.TotalUsage)
            {
                // Nanoseconds of CPU consumed per elapsed second — directly
                // comparable to the NanoCPUs limit, so the UI can show a real
                // fraction of the allocation rather than a host-relative percentage.
                cpuNanos = (long)((total - previous.TotalUsage) / elapsed);
            }
        }

        _cpuBaseline[containerId] = (total, now);

        return new ContainerStatsSample(
            ContainerId: containerId,
            SampledAt: now,
            CpuNanos: cpuNanos,
            MemoryBytes: (long)(captured.MemoryStats?.Usage ?? 0),
            MemoryLimitBytes: (long)(captured.MemoryStats?.Limit ?? 0));
    }

    public async Task<ExecResult> ExecAsync(ExecRequest request, Stream? standardOutput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var created = await client.Exec.ExecCreateContainerAsync(
                request.ContainerId,
                DockerExec.ToCreateParameters(request),
                ct).ConfigureAwait(false);

            using var stream = await client.Exec
                .StartAndAttachContainerExecAsync(created.ID, tty: false, ct)
                .ConfigureAwait(false);

            using var stderr = new MemoryStream();

            // Stdout must be consumed even when the caller does not want it:
            // leaving it unread stalls the daemon-side writer and the exec never
            // completes.
            await stream
                .CopyOutputToAsync(Stream.Null, standardOutput ?? Stream.Null, stderr, ct)
                .ConfigureAwait(false);

            var inspect = await client.Exec.InspectContainerExecAsync(created.ID, ct).ConfigureAwait(false);

            if (standardOutput is not null)
            {
                await standardOutput.FlushAsync(ct).ConfigureAwait(false);
            }

            return new ExecResult((int)inspect.ExitCode, Encoding.UTF8.GetString(stderr.ToArray()));
        }
        catch (DD.DockerApiException ex)
        {
            throw new ContainerRuntimeException($"Exec failed in container {request.ContainerId}.", ex);
        }
    }
}
