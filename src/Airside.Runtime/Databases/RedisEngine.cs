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
    };

    public override IReadOnlyList<string> SupportedVersions { get; } = ["7.4", "7.2"];

    public override ImageReference ResolveImage(string version) => new("redis", $"{version}-alpine");

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
            Image = ResolveImage(spec.Version),
            Labels = context.Labels,
            Command = command,
            Limits = new ContainerLimits(spec.MemoryBytes, spec.CpuNanos),
            NetworkName = context.NetworkName,
            RestartPolicy = spec.AutoRestart ? RestartPolicy.UnlessStopped : RestartPolicy.No,
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
