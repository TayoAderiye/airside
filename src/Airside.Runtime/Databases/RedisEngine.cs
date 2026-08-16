using System.Globalization;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;

namespace Airside.Runtime.Databases;

/// <summary>
/// Redis, which differs from the other three in ways that produce a broken
/// abstraction if ignored.
/// </summary>
/// <remarks>
/// No database name, no username, <c>maxmemory</c> as a separate resource axis,
/// snapshot backups rather than dumps, and a restore that requires a stop. None
/// of that is special-cased by callers — they read <see cref="Capabilities"/>,
/// and this class simply answers differently.
/// </remarks>
internal sealed class RedisEngine(IContainerRuntime runtime) : DatabaseEngineBase(runtime)
{
    /// <summary>
    /// Policies Redis accepts. <c>noeviction</c> is included and is the Redis
    /// default, but a full instance under it starts failing writes rather than
    /// evicting — the API returns a warning saying so.
    /// </summary>
    public static IReadOnlyList<string> MaxMemoryPolicies { get; } =
    [
        "noeviction",
        "allkeys-lru", "allkeys-lfu", "allkeys-random",
        "volatile-lru", "volatile-lfu", "volatile-random", "volatile-ttl",
    ];

    public override DatabaseEngineKind Kind => DatabaseEngineKind.Redis;

    public override DatabaseEngineCapabilities Capabilities { get; } = new()
    {
        // 16 numbered logical databases, selected at connect time. There is
        // nothing to name.
        SupportsDatabaseName = false,

        // requirepass authenticates the implicit `default` user. ACL users are a
        // later addition and arrive as extra credential rows, not a schema change.
        SupportsUserAccounts = false,

        // No pg_dump equivalent: BGSAVE produces an RDB file.
        SupportsLogicalBackup = false,
        SupportsSnapshotBackup = true,

        // An RDB cannot be loaded into a running instance.
        RequiresStopForRestore = true,

        RequiresMaxMemory = true,
        QueryDialect = QueryDialect.RedisCommand,
        DefaultPort = 6379,
        DefaultEnvKeyPrefix = "REDIS",
        EvictionPolicies = MaxMemoryPolicies,
        SupportedVariants = [ImageVariant.Alpine, ImageVariant.Debian],
        DefaultVariant = ImageVariant.Alpine,
    };

    public override IReadOnlyList<string> SupportedVersions { get; } = ["7.4", "7.2"];

    public override ImageReference ResolveImage(string version, ImageVariant variant) => new(
        "redis",
        variant == ImageVariant.Alpine ? $"{version}-alpine" : version);

    public override Result Validate(DatabaseProvisionSpec spec)
    {
        var baseResult = base.Validate(spec);

        if (baseResult.IsFailure)
        {
            return baseResult;
        }

        if (string.IsNullOrWhiteSpace(spec!.MaxMemoryPolicy))
        {
            return Required("maxMemoryPolicy", "Redis requires a maxmemory-policy.");
        }

        if (!MaxMemoryPolicies.Contains(spec.MaxMemoryPolicy, StringComparer.Ordinal))
        {
            return NotApplicable(
                ErrorCodes.ValidationFailed,
                "maxMemoryPolicy",
                $"'{spec.MaxMemoryPolicy}' is not a Redis maxmemory-policy.",
                supported: MaxMemoryPolicies);
        }

        if (spec.MaxMemoryBytes >= spec.MemoryBytes)
        {
            return NotApplicable(
                ErrorCodes.ValidationFailed,
                "maxMemoryBytes",
                "maxmemory must be below the container memory limit. Redis needs headroom above the "
                + "dataset for client buffers, replication buffers, and fragmentation; setting them "
                + "equal guarantees the kernel kills the container before Redis ever evicts.");
        }

        return Result.Ok();
    }

    /// <summary>
    /// 70% of the container limit when persistence is on, 80% when it is off.
    /// </summary>
    /// <remarks>
    /// <c>maxmemory</c> bounds the dataset only. During BGSAVE or an AOF rewrite
    /// Redis forks, and copy-on-write grows the child's resident set toward the
    /// parent's as writes land — so peak usage approaches twice the dataset on a
    /// write-heavy instance. At 80% with persistence enabled the cgroup OOM killer
    /// takes the container mid-backup and it restarts, which reads as a mystery
    /// crash rather than a backup problem. 80% is right only for a pure cache with
    /// both RDB and AOF off.
    /// </remarks>
    public override MaxMemoryRecommendation RecommendMaxMemory(long containerMemoryBytes, bool persistenceEnabled)
    {
        var fraction = persistenceEnabled ? 0.70 : 0.80;

        var reason = persistenceEnabled
            ? "70% of the container limit. Redis forks during BGSAVE and AOF rewrite, and copy-on-write "
              + "can push peak memory well above the dataset size; the remaining headroom is what stops "
              + "the kernel killing the container mid-backup."
            : "80% of the container limit. With RDB and AOF both disabled Redis never forks, so less "
              + "headroom is needed — but this instance cannot be backed up.";

        return new MaxMemoryRecommendation((long)(containerMemoryBytes * fraction), fraction, reason);
    }

    public override ContainerSpec BuildContainerSpec(DatabaseProvisionSpec spec, ProvisionContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);

        var aof = spec.AofEnabled == true;

        List<string> command =
        [
            "redis-server",
            "--maxmemory", spec.MaxMemoryBytes!.Value.ToString(CultureInfo.InvariantCulture),
            "--maxmemory-policy", spec.MaxMemoryPolicy!,
            "--appendonly", aof ? "yes" : "no",

            // requirepass has to be an argument: the official image reads no
            // password environment variable, and the alternative is a config file
            // written into the volume, which needs a running container to write
            // into and so cannot happen before first start.
            //
            // The exposure is bounded and worth stating precisely: the value is
            // visible in this container's own process list and in `docker
            // inspect`. Nothing in another container can see it — PID namespaces
            // are separate — so reading it requires either Docker socket access,
            // which is already root-equivalent, or permission to exec into this
            // container, which Airside gates. The config-file approach lands with
            // volume file writes in Phase 3.
            "--requirepass", spec.Password.Reveal(),
        ];

        if (!aof)
        {
            // Redis's default save points would otherwise fork for RDB snapshots
            // even on an instance the admin has told us is a pure cache, which is
            // exactly the fork this instance has no memory headroom for.
            command.AddRange(["--save", string.Empty]);
        }

        return new ContainerSpec
        {
            Name = context.ContainerName,
            Image = ResolveImage(spec.Version, spec.Variant ?? Capabilities.DefaultVariant),
            Labels = context.Labels,
            Command = command,
            Limits = new ContainerLimits(spec.MemoryBytes, spec.CpuNanos),
            NetworkName = context.NetworkName,
            RestartPolicy = spec.AutoRestart ? RestartPolicy.UnlessStopped : RestartPolicy.No,
            Security = ContainerSecurity.DatabaseEngine,
            Mounts = [new VolumeMount(context.DataVolumeName, "/data")],
            Ports = DatabasePorts.For(spec, Capabilities.DefaultPort),

            // redis-cli reads the password from the environment rather than an
            // argument, so the health check does not repeat the exposure above.
            Environment = Entries(("REDISCLI_AUTH", spec.Password.Reveal(), true)),
            HealthProbe = new HealthProbe(
                ["redis-cli", "ping"],
                Interval: TimeSpan.FromSeconds(5),
                Timeout: TimeSpan.FromSeconds(3),
                Retries: 10,
                StartPeriod: TimeSpan.FromSeconds(5)),
        };
    }

    public override ConnectionDetails BuildConnectionDetails(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        // No username in the URL: the default user is implicit under requirepass.
        var url = $"redis://:{Uri.EscapeDataString(credential.Password.Reveal())}@"
            + $"{endpoint.HostName}:{endpoint.Port}";

        return new ConnectionDetails(
            endpoint.HostName,
            endpoint.Port,
            DatabaseName: null,
            Username: null,
            credential.Password,
            new Secret(url));
    }

    /// <summary>
    /// <c>REDIS_HOST</c>, <c>REDIS_PORT</c>, <c>REDIS_PASSWORD</c>,
    /// <c>REDIS_URL</c> — and deliberately no <c>_NAME</c> or <c>_USER</c>,
    /// because Redis has neither. Emitting empty ones to match the SQL engines
    /// would be the broken abstraction this design exists to avoid.
    /// </summary>
    public override IReadOnlyList<EnvironmentEntry> BuildInjectedEnvironment(
        string keyPrefix,
        ConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        return Entries(
            ($"{keyPrefix}_HOST", details.Host, false),
            ($"{keyPrefix}_PORT", details.Port.ToString(CultureInfo.InvariantCulture), false),
            ($"{keyPrefix}_PASSWORD", details.Password.Reveal(), true),
            ($"{keyPrefix}_URL", details.ConnectionString.Reveal(), true));
    }

    /// <summary>
    /// A snapshot, not a dump: BGSAVE, wait for it to finish, copy the RDB out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no pg_dump equivalent. BGSAVE returns immediately and forks, so
    /// the flow has to poll <c>INFO persistence</c> until
    /// <c>rdb_bgsave_in_progress</c> clears — copying the file before then
    /// captures a half-written RDB that loads as a truncated dataset.
    /// </para>
    /// <para>
    /// <c>rdb_last_bgsave_status</c> is checked too. BGSAVE can fail after
    /// starting — most often because the fork could not get memory, which is the
    /// exact failure the 70% maxmemory default exists to avoid — and a failed
    /// BGSAVE leaves the previous RDB in place, so copying it out would silently
    /// produce a backup of whatever the state was hours ago.
    /// </para>
    /// </remarks>
    public override async Task<BackupArtifact> BackupAsync(
        BackupOperation operation,
        Stream destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(destination);

        var container = operation.Endpoint.ContainerId;
        var auth = Entries(("REDISCLI_AUTH", operation.Credential.Password.Reveal(), true));

        var lastSaveBefore = await ReadInfoFieldAsync(container, auth, "rdb_last_save_time", ct)
            .ConfigureAwait(false);

        var trigger = await Runtime.Containers
            .ExecAsync(new ExecRequest(container, ["redis-cli", "BGSAVE"], auth), null, ct)
            .ConfigureAwait(false);

        if (trigger.ExitCode != 0)
        {
            throw new InvalidOperationException($"BGSAVE could not be started: {trigger.StandardError}");
        }

        operation.Progress?.Report("BGSAVE started; waiting for the fork to finish.");

        var deadline = DateTimeOffset.UtcNow.Add(BgSaveTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var inProgress = await ReadInfoFieldAsync(container, auth, "rdb_bgsave_in_progress", ct)
                .ConfigureAwait(false);

            if (string.Equals(inProgress, "0", StringComparison.Ordinal))
            {
                var status = await ReadInfoFieldAsync(container, auth, "rdb_last_bgsave_status", ct)
                    .ConfigureAwait(false);

                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "BGSAVE reported failure. The most common cause is the fork being unable to "
                        + "obtain memory; the on-disk RDB is stale and must not be used as a backup.");
                }

                var lastSaveAfter = await ReadInfoFieldAsync(container, auth, "rdb_last_save_time", ct)
                    .ConfigureAwait(false);

                if (string.Equals(lastSaveBefore, lastSaveAfter, StringComparison.Ordinal))
                {
                    // Never advanced: what is on disk is the previous snapshot, not
                    // this one. Copying it would produce a backup silently hours old.
                    throw new InvalidOperationException(
                        "BGSAVE completed but the save timestamp did not advance, so the RDB on disk "
                        + "is not this snapshot.");
                }

                break;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        using var hashing = new HashingStream(destination);
        await Runtime.Volumes
            .CopyFromAsync(operation.DataVolumeName, RdbFileName, hashing, ct)
            .ConfigureAwait(false);

        if (hashing.BytesWritten == 0)
        {
            throw new InvalidOperationException("The RDB file was empty. Refusing to record an empty backup.");
        }

        return new BackupArtifact(
            hashing.BytesWritten, hashing.Hash, operation.EngineSnapshot, BackupKind.Snapshot);
    }

    /// <summary>
    /// Stop, replace the RDB, start.
    /// </summary>
    /// <remarks>
    /// An RDB cannot be loaded into a running instance — Redis reads it once, at
    /// startup. The container is already stopped by the caller because
    /// <c>RequiresStopForRestore</c> says so; this writes the file and leaves
    /// starting to the caller, so the stop and the start bracket the whole
    /// operation rather than being buried in the middle of it.
    /// </remarks>
    public override async Task RestoreAsync(RestoreOperation operation, Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(source);

        var container = await Runtime.Containers
            .FindAsync(operation.Endpoint.ContainerId, ct)
            .ConfigureAwait(false);

        if (container?.State == ContainerRunState.Running)
        {
            // A guard, not a convenience. Writing dump.rdb underneath a running
            // Redis does nothing until the next restart and then silently loses
            // everything written since — the worst possible outcome, because it
            // looks like it worked.
            throw new InvalidOperationException(
                "The Redis container is still running. An RDB restore requires the instance to be "
                + "stopped first, or the file is ignored and then overwritten.");
        }

        operation.Progress?.Report("Replacing dump.rdb in the data volume.");
        await Runtime.Volumes
            .CopyIntoAsync(operation.DataVolumeName, RdbFileName, source, ct)
            .ConfigureAwait(false);
    }

    public override async Task RotatePasswordAsync(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue current,
        Secret replacement,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);

        // CONFIG SET requirepass takes effect immediately but does not survive a
        // restart: the container's command line still carries the old value. The
        // caller therefore recreates the container afterwards, and the platform
        // treats rotation as a change that requires a restart rather than
        // pretending it is live-only.
        var result = await Runtime.Containers.ExecAsync(
            new ExecRequest(
                endpoint.ContainerId,
                ["redis-cli", "CONFIG", "SET", "requirepass", replacement.Reveal()],
                Entries(("REDISCLI_AUTH", current.Password.Reveal(), true))),
            null,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Password rotation failed: {result.StandardError}");
        }
    }

    private const string RdbFileName = "dump.rdb";

    private static readonly TimeSpan BgSaveTimeout = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Reads one field out of <c>INFO persistence</c>.</summary>
    private async Task<string?> ReadInfoFieldAsync(
        string containerId,
        IReadOnlyList<EnvironmentEntry> auth,
        string field,
        CancellationToken ct)
    {
        using var output = new MemoryStream();

        var result = await Runtime.Containers
            .ExecAsync(new ExecRequest(containerId, ["redis-cli", "INFO", "persistence"], auth), output, ct)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"INFO persistence failed: {result.StandardError}");
        }

        var text = System.Text.Encoding.UTF8.GetString(output.ToArray());

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(field + ":", StringComparison.Ordinal))
            {
                return trimmed[(field.Length + 1)..].Trim();
            }
        }

        return null;
    }
}

internal sealed class DatabaseEngineRegistry : IDatabaseEngineRegistry
{
    private readonly Dictionary<DatabaseEngineKind, IDatabaseEngine> _engines;

    public DatabaseEngineRegistry(IEnumerable<IDatabaseEngine> engines)
    {
        _engines = engines.ToDictionary(e => e.Kind);
        All = [.. _engines.Values.OrderBy(e => e.Kind)];
    }

    public IReadOnlyList<IDatabaseEngine> All { get; }

    public IDatabaseEngine Get(DatabaseEngineKind kind) =>
        _engines.TryGetValue(kind, out var engine)
            ? engine
            : throw new NotSupportedException($"No engine is registered for {kind}.");
}
