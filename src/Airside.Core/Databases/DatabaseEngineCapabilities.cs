namespace Airside.Core.Databases;

public enum DatabaseEngineKind
{
    Postgres,
    MySql,
    MongoDb,
    Redis,
}

public enum QueryDialect
{
    Sql,
    MongoShell,
    RedisCommand,
}

/// <summary>
/// What an engine can and cannot do.
/// </summary>
/// <remarks>
/// <para>
/// Nothing outside an engine implementation switches on
/// <see cref="DatabaseEngineKind"/>. The provisioning validator, the backup
/// scheduler, the restore flow, and the query console all read capabilities
/// instead. Redis is not a special case in the calling code — it simply answers
/// these questions differently, which is the only way an abstraction survives an
/// engine that does not fit the shape of the other three.
/// </para>
/// </remarks>
public sealed record DatabaseEngineCapabilities
{
    /// <summary>False for Redis: 16 numbered logical databases, selected at connect time.</summary>
    public required bool SupportsDatabaseName { get; init; }

    /// <summary>
    /// False for Redis in the MVP — <c>requirepass</c> authenticates the implicit
    /// <c>default</c> user. Redis 6+ ACL users arrive as additional credential
    /// rows without a change to this shape.
    /// </summary>
    public required bool SupportsUserAccounts { get; init; }

    /// <summary>True where a dump-and-restore tool exists: pg_dump, mysqldump, mongodump.</summary>
    public required bool SupportsLogicalBackup { get; init; }

    /// <summary>True for Redis: BGSAVE to an RDB file, not a dump.</summary>
    public required bool SupportsSnapshotBackup { get; init; }

    /// <summary>True for Redis: an RDB cannot be restored into a running instance.</summary>
    public required bool RequiresStopForRestore { get; init; }

    /// <summary>
    /// True for Redis. <c>maxmemory</c> is a separate resource axis from the
    /// container limit; without it, Redis grows until the cgroup OOM killer takes
    /// it and the restart looks like an unexplained crash.
    /// </summary>
    public required bool RequiresMaxMemory { get; init; }

    public required QueryDialect QueryDialect { get; init; }

    public required int DefaultPort { get; init; }

    /// <summary>
    /// The default prefix for injected environment keys — <c>DATABASE</c> for the
    /// SQL engines, <c>MONGO</c>, <c>REDIS</c>. Editable per attachment so two
    /// attached databases cannot fight over <c>DATABASE_URL</c>.
    /// </summary>
    public required string DefaultEnvKeyPrefix { get; init; }

    /// <summary>
    /// Allowed values for an engine that has an eviction policy, null for one
    /// that does not. Exposed through the capability rather than fetched from a
    /// concrete engine type, so the provisioning form stays driven entirely by
    /// what an engine says about itself.
    /// </summary>
    public IReadOnlyList<string>? EvictionPolicies { get; init; }
}
