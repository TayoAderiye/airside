using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;

namespace Airside.Runtime.Databases;

/// <summary>
/// Shared scaffolding for the engines. Nothing here branches on engine kind —
/// that is the whole point of the capability model.
/// </summary>
internal abstract class DatabaseEngineBase(IContainerRuntime runtime) : IDatabaseEngine
{
    protected IContainerRuntime Runtime { get; } = runtime;

    public abstract DatabaseEngineKind Kind { get; }

    public abstract DatabaseEngineCapabilities Capabilities { get; }

    public abstract IReadOnlyList<string> SupportedVersions { get; }

    public abstract ImageReference ResolveImage(string version);

    public abstract ContainerSpec BuildContainerSpec(DatabaseProvisionSpec spec, ProvisionContext context);

    public abstract ConnectionDetails BuildConnectionDetails(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue credential);

    public abstract IReadOnlyList<EnvironmentEntry> BuildInjectedEnvironment(
        string keyPrefix,
        ConnectionDetails details);

    /// <summary>
    /// Validates the request against this engine's capabilities.
    /// </summary>
    /// <remarks>
    /// Inapplicable fields are rejected, not ignored. A caller sending
    /// <c>databaseName</c> to Redis has misunderstood something, and saying so is
    /// more useful than silently dropping the value and letting them discover the
    /// mismatch when the connection string does not work.
    /// </remarks>
    public virtual Result Validate(DatabaseProvisionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!SupportedVersions.Contains(spec.Version, StringComparer.Ordinal))
        {
            return NotApplicable(
                ErrorCodes.ValidationUnsupportedVersion,
                "version",
                $"{Kind} {spec.Version} is not a supported version.",
                supported: SupportedVersions);
        }

        if (Capabilities.SupportsDatabaseName && string.IsNullOrWhiteSpace(spec.DatabaseName))
        {
            return Required("databaseName", $"{Kind} requires a database name.");
        }

        if (!Capabilities.SupportsDatabaseName && !string.IsNullOrWhiteSpace(spec.DatabaseName))
        {
            return NotApplicable(
                ErrorCodes.ValidationFieldNotApplicable,
                "databaseName",
                $"{Kind} has no database name. It exposes 16 numbered logical databases selected at connect time.");
        }

        if (Capabilities.SupportsUserAccounts && string.IsNullOrWhiteSpace(spec.Username))
        {
            return Required("username", $"{Kind} requires a username.");
        }

        if (!Capabilities.SupportsUserAccounts && !string.IsNullOrWhiteSpace(spec.Username))
        {
            return NotApplicable(
                ErrorCodes.ValidationFieldNotApplicable,
                "username",
                $"{Kind} authenticates with a password only; there is no username to set.");
        }

        if (Capabilities.RequiresMaxMemory && spec.MaxMemoryBytes is null)
        {
            return Required(
                "maxMemoryBytes",
                $"{Kind} requires maxmemory. Without it the instance grows until the container's "
                + "memory limit is hit and the kernel kills it.");
        }

        if (!Capabilities.RequiresMaxMemory && spec.MaxMemoryBytes is not null)
        {
            return NotApplicable(
                ErrorCodes.ValidationFieldNotApplicable,
                "maxMemoryBytes",
                $"maxmemory applies to Redis only; {Kind} is bounded by the container memory limit.");
        }

        return Result.Ok();
    }

    public virtual MaxMemoryRecommendation? RecommendMaxMemory(long containerMemoryBytes, bool persistenceEnabled) =>
        null;

    /// <summary>
    /// Readiness is the container's own health check, which each engine defines
    /// using the client already present in its image.
    /// </summary>
    /// <remarks>
    /// Deliberately not a client-library connection: adding four database drivers
    /// to the control plane to answer "is it up?" is a large dependency surface
    /// for a question the engine's own tooling already answers correctly.
    /// </remarks>
    public async Task<DatabaseProbeResult> ProbeAsync(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue credential,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var container = await Runtime.Containers.FindAsync(endpoint.ContainerId, ct).ConfigureAwait(false);

        if (container is null)
        {
            return new DatabaseProbeResult(false, false, "The container no longer exists.");
        }

        var running = container.State == ContainerRunState.Running;

        return container.Health switch
        {
            ContainerHealth.Healthy => new DatabaseProbeResult(true, true, null),
            ContainerHealth.Starting => new DatabaseProbeResult(false, false, "Still starting up."),
            ContainerHealth.Unhealthy => new DatabaseProbeResult(running, false, "Health check is failing."),
            _ => new DatabaseProbeResult(running, running, running ? null : "Container is not running."),
        };
    }

    public virtual Task<BackupArtifact> BackupAsync(
        BackupOperation operation,
        Stream destination,
        CancellationToken ct) =>
        throw new NotSupportedException($"Backups for {Kind} arrive in Phase 3.");

    public virtual Task RestoreAsync(RestoreOperation operation, Stream source, CancellationToken ct) =>
        throw new NotSupportedException($"Restores for {Kind} arrive in Phase 3.");

    public virtual Task RotatePasswordAsync(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue current,
        Secret replacement,
        CancellationToken ct) =>
        throw new NotSupportedException($"Credential rotation for {Kind} arrives in Phase 3.");

    protected static Result Required(string field, string message) => new Error(
        ErrorCodes.ValidationFieldRequired,
        message,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = field });

    protected static Result NotApplicable(
        string code,
        string field,
        string message,
        IReadOnlyList<string>? supported = null)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = field };

        if (supported is not null)
        {
            metadata["supported"] = supported;
        }

        return new Error(code, message, metadata);
    }

    protected static IReadOnlyList<EnvironmentEntry> Entries(params (string Key, string Value, bool Sensitive)[] items) =>
        [.. items.Select(i => new EnvironmentEntry(i.Key, new Secret(i.Value), i.Sensitive))];

    /// <summary>
    /// Guards a password that is about to be embedded as a SQL or shell-free
    /// literal in a rotation statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rotation passwords are always generated by Airside, and
    /// <c>SecretGenerator</c>'s alphabet deliberately excludes quotes,
    /// backslashes, and the URI-reserved characters — so a literal is safe by
    /// construction rather than by escaping. This check enforces that
    /// construction instead of assuming it: if the alphabet ever widens, rotation
    /// fails loudly here rather than silently emitting a statement an attacker
    /// could have shaped.
    /// </para>
    /// <para>
    /// Parameterisation would be better, but no engine offers it for
    /// <c>ALTER USER … PASSWORD</c>, and psql's <c>:'var'</c> interpolation does
    /// not apply to <c>--command</c> — only to scripts, which would put the
    /// password on disk instead.
    /// </para>
    /// </remarks>
    private static readonly System.Buffers.SearchValues<char> UnsafeLiteralChars =
        System.Buffers.SearchValues.Create("'\\\"`$;\n\r\0");

    protected static string SafeLiteral(Secret password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var value = password.Reveal();

        if (value.AsSpan().IndexOfAny(UnsafeLiteralChars) >= 0)
        {
            throw new InvalidOperationException(
                "The replacement password contains a character that cannot be embedded safely in a "
                + "rotation statement. Airside-generated passwords never do; this indicates the "
                + "generator's alphabet has changed.");
        }

        return value;
    }
}

internal sealed class PostgresEngine(IContainerRuntime runtime) : DatabaseEngineBase(runtime)
{
    public override DatabaseEngineKind Kind => DatabaseEngineKind.Postgres;

    public override DatabaseEngineCapabilities Capabilities { get; } = new()
    {
        SupportsDatabaseName = true,
        SupportsUserAccounts = true,
        SupportsLogicalBackup = true,
        SupportsSnapshotBackup = false,
        RequiresStopForRestore = false,
        RequiresMaxMemory = false,
        QueryDialect = QueryDialect.Sql,
        DefaultPort = 5432,
        DefaultEnvKeyPrefix = "DATABASE",
    };

    public override IReadOnlyList<string> SupportedVersions { get; } = ["17", "16", "15"];

    public override ImageReference ResolveImage(string version) => new("postgres", $"{version}-alpine");

    public override ContainerSpec BuildContainerSpec(DatabaseProvisionSpec spec, ProvisionContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);

        return new ContainerSpec
        {
            Name = context.ContainerName,
            Image = ResolveImage(spec.Version),
            Labels = context.Labels,
            Limits = new ContainerLimits(spec.MemoryBytes, spec.CpuNanos),
            NetworkName = context.NetworkName,
            RestartPolicy = spec.AutoRestart ? RestartPolicy.UnlessStopped : RestartPolicy.No,
            Security = ContainerSecurity.DatabaseEngine,
            Mounts = [new VolumeMount(context.DataVolumeName, "/var/lib/postgresql/data")],
            Ports = DatabasePorts.For(spec, Capabilities.DefaultPort),

            // The image's documented mechanism. Environment rather than argv, so
            // the password is not in the container's own process list.
            Environment = Entries(
                ("POSTGRES_DB", spec.DatabaseName!, false),
                ("POSTGRES_USER", spec.Username!, false),
                ("POSTGRES_PASSWORD", spec.Password.Reveal(), true)),

            // pg_isready ships in the image. An argument vector, never a shell.
            HealthProbe = new HealthProbe(
                ["pg_isready", "-U", spec.Username!, "-d", spec.DatabaseName!],
                Interval: TimeSpan.FromSeconds(5),
                Timeout: TimeSpan.FromSeconds(5),
                Retries: 10,
                StartPeriod: TimeSpan.FromSeconds(10)),
        };
    }

    public override ConnectionDetails BuildConnectionDetails(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var url = $"postgresql://{Uri.EscapeDataString(credential.Username!)}:"
            + $"{Uri.EscapeDataString(credential.Password.Reveal())}@"
            + $"{endpoint.HostName}:{endpoint.Port}/{endpoint.DatabaseName}";

        return new ConnectionDetails(
            endpoint.HostName, endpoint.Port, endpoint.DatabaseName,
            credential.Username, credential.Password, new Secret(url));
    }

    public override IReadOnlyList<EnvironmentEntry> BuildInjectedEnvironment(
        string keyPrefix,
        ConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        return Entries(
            ($"{keyPrefix}_HOST", details.Host, false),
            ($"{keyPrefix}_PORT", details.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
            ($"{keyPrefix}_NAME", details.DatabaseName!, false),
            ($"{keyPrefix}_USER", details.Username!, false),
            ($"{keyPrefix}_PASSWORD", details.Password.Reveal(), true),
            ($"{keyPrefix}_URL", details.ConnectionString.Reveal(), true));
    }

    public override Task<BackupArtifact> BackupAsync(
        BackupOperation operation,
        Stream destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // --clean --if-exists so the dump can be replayed into a database that
        // already has objects, which is what a restore always faces.
        return LogicalBackup.RunAsync(
            Runtime,
            operation.Endpoint.ContainerId,
            [
                "pg_dump", "--username", operation.Credential.Username!, "--dbname",
                operation.Endpoint.DatabaseName!, "--clean", "--if-exists", "--no-owner", "--no-privileges",
            ],
            // PGPASSWORD, not --password: an argument is visible in the
            // container's process list to anything else running in it.
            Entries(("PGPASSWORD", operation.Credential.Password.Reveal(), true)),
            destination,
            operation.EngineSnapshot,
            operation.Progress,
            ct);
    }

    public override Task RestoreAsync(RestoreOperation operation, Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return LogicalBackup.RestoreAsync(
            Runtime,
            operation.Endpoint.ContainerId,
            [
                "psql", "--username", operation.Credential.Username!, "--dbname",
                operation.Endpoint.DatabaseName!, "--file", $"/tmp/{LogicalBackup.StagingFile}",
                "--set", "ON_ERROR_STOP=1",
            ],
            Entries(("PGPASSWORD", operation.Credential.Password.Reveal(), true)),
            source,
            ct);
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

        var result = await Runtime.Containers.ExecAsync(
            new ExecRequest(
                endpoint.ContainerId,
                [
                    "psql", "--username", current.Username!, "--dbname", endpoint.DatabaseName!,
                    "--set", "ON_ERROR_STOP=1",
                    "--command", $"ALTER USER CURRENT_USER WITH PASSWORD '{SafeLiteral(replacement)}'",
                ],
                Entries(("PGPASSWORD", current.Password.Reveal(), true))),
            null,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Password rotation failed: {result.StandardError}");
        }
    }
}

internal sealed class MySqlEngine(IContainerRuntime runtime) : DatabaseEngineBase(runtime)
{
    public override DatabaseEngineKind Kind => DatabaseEngineKind.MySql;

    public override DatabaseEngineCapabilities Capabilities { get; } = new()
    {
        SupportsDatabaseName = true,
        SupportsUserAccounts = true,
        SupportsLogicalBackup = true,
        SupportsSnapshotBackup = false,
        RequiresStopForRestore = false,
        RequiresMaxMemory = false,
        QueryDialect = QueryDialect.Sql,
        DefaultPort = 3306,
        DefaultEnvKeyPrefix = "DATABASE",
    };

    public override IReadOnlyList<string> SupportedVersions { get; } = ["8.4", "8.0"];

    public override ImageReference ResolveImage(string version) => new("mysql", version);

    public override ContainerSpec BuildContainerSpec(DatabaseProvisionSpec spec, ProvisionContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);

        return new ContainerSpec
        {
            Name = context.ContainerName,
            Image = ResolveImage(spec.Version),
            Labels = context.Labels,
            Limits = new ContainerLimits(spec.MemoryBytes, spec.CpuNanos),
            NetworkName = context.NetworkName,
            RestartPolicy = spec.AutoRestart ? RestartPolicy.UnlessStopped : RestartPolicy.No,
            Security = ContainerSecurity.DatabaseEngine,
            Mounts = [new VolumeMount(context.DataVolumeName, "/var/lib/mysql")],
            Ports = DatabasePorts.For(spec, Capabilities.DefaultPort),
            Environment = Entries(
                ("MYSQL_DATABASE", spec.DatabaseName!, false),
                ("MYSQL_USER", spec.Username!, false),
                ("MYSQL_PASSWORD", spec.Password.Reveal(), true),
                ("MYSQL_ROOT_PASSWORD", spec.Password.Reveal(), true)),
            HealthProbe = new HealthProbe(
                ["mysqladmin", "ping", "-h", "127.0.0.1"],
                Interval: TimeSpan.FromSeconds(5),
                Timeout: TimeSpan.FromSeconds(5),
                Retries: 10,
                StartPeriod: TimeSpan.FromSeconds(30)),
        };
    }

    public override ConnectionDetails BuildConnectionDetails(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var url = $"mysql://{Uri.EscapeDataString(credential.Username!)}:"
            + $"{Uri.EscapeDataString(credential.Password.Reveal())}@"
            + $"{endpoint.HostName}:{endpoint.Port}/{endpoint.DatabaseName}";

        return new ConnectionDetails(
            endpoint.HostName, endpoint.Port, endpoint.DatabaseName,
            credential.Username, credential.Password, new Secret(url));
    }

    public override IReadOnlyList<EnvironmentEntry> BuildInjectedEnvironment(
        string keyPrefix,
        ConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        return Entries(
            ($"{keyPrefix}_HOST", details.Host, false),
            ($"{keyPrefix}_PORT", details.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
            ($"{keyPrefix}_NAME", details.DatabaseName!, false),
            ($"{keyPrefix}_USER", details.Username!, false),
            ($"{keyPrefix}_PASSWORD", details.Password.Reveal(), true),
            ($"{keyPrefix}_URL", details.ConnectionString.Reveal(), true));
    }

    public override Task<BackupArtifact> BackupAsync(
        BackupOperation operation,
        Stream destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return LogicalBackup.RunAsync(
            Runtime,
            operation.Endpoint.ContainerId,
            [
                "mysqldump", "--user", operation.Credential.Username!, "--host", "127.0.0.1",
                "--single-transaction", "--routines", "--triggers", "--events",
                operation.Endpoint.DatabaseName!,
            ],
            // MYSQL_PWD rather than --password: an argument would be visible in
            // the container's process list, and mysqldump warns about it anyway.
            Entries(("MYSQL_PWD", operation.Credential.Password.Reveal(), true)),
            destination,
            operation.EngineSnapshot,
            operation.Progress,
            ct);
    }

    public override Task RestoreAsync(RestoreOperation operation, Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return LogicalBackup.RestoreAsync(
            Runtime,
            operation.Endpoint.ContainerId,
            [
                "mysql", "--user", operation.Credential.Username!, "--host", "127.0.0.1",
                "--database", operation.Endpoint.DatabaseName!,
                "--execute", $"SOURCE /tmp/{LogicalBackup.StagingFile}",
            ],
            Entries(("MYSQL_PWD", operation.Credential.Password.Reveal(), true)),
            source,
            ct);
    }

    public override async Task RotatePasswordAsync(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue current,
        Secret replacement,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(current);

        var result = await Runtime.Containers.ExecAsync(
            new ExecRequest(
                endpoint.ContainerId,
                [
                    "mysql", "--user", current.Username!, "--host", "127.0.0.1",
                    "--execute", $"ALTER USER CURRENT_USER() IDENTIFIED BY '{SafeLiteral(replacement)}'",
                ],
                Entries(("MYSQL_PWD", current.Password.Reveal(), true))),
            null,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Password rotation failed: {result.StandardError}");
        }
    }
}

internal sealed class MongoDbEngine(IContainerRuntime runtime) : DatabaseEngineBase(runtime)
{
    public override DatabaseEngineKind Kind => DatabaseEngineKind.MongoDb;

    public override DatabaseEngineCapabilities Capabilities { get; } = new()
    {
        SupportsDatabaseName = true,
        SupportsUserAccounts = true,
        SupportsLogicalBackup = true,
        SupportsSnapshotBackup = false,
        RequiresStopForRestore = false,
        RequiresMaxMemory = false,
        QueryDialect = QueryDialect.MongoShell,
        DefaultPort = 27017,
        DefaultEnvKeyPrefix = "MONGO",
    };

    public override IReadOnlyList<string> SupportedVersions { get; } = ["8.0", "7.0"];

    public override ImageReference ResolveImage(string version) => new("mongo", version);

    public override ContainerSpec BuildContainerSpec(DatabaseProvisionSpec spec, ProvisionContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);

        return new ContainerSpec
        {
            Name = context.ContainerName,
            Image = ResolveImage(spec.Version),
            Labels = context.Labels,
            Limits = new ContainerLimits(spec.MemoryBytes, spec.CpuNanos),
            NetworkName = context.NetworkName,
            RestartPolicy = spec.AutoRestart ? RestartPolicy.UnlessStopped : RestartPolicy.No,
            Security = ContainerSecurity.DatabaseEngine,
            Mounts = [new VolumeMount(context.DataVolumeName, "/data/db")],
            Ports = DatabasePorts.For(spec, Capabilities.DefaultPort),
            Environment = Entries(
                ("MONGO_INITDB_DATABASE", spec.DatabaseName!, false),
                ("MONGO_INITDB_ROOT_USERNAME", spec.Username!, false),
                ("MONGO_INITDB_ROOT_PASSWORD", spec.Password.Reveal(), true)),

            // ping runs against the admin database and needs no credentials, so
            // the health check does not have to carry a password at all.
            HealthProbe = new HealthProbe(
                ["mongosh", "--quiet", "--eval", "db.adminCommand('ping')"],
                Interval: TimeSpan.FromSeconds(5),
                Timeout: TimeSpan.FromSeconds(5),
                Retries: 10,
                StartPeriod: TimeSpan.FromSeconds(20)),
        };
    }

    public override ConnectionDetails BuildConnectionDetails(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        // authSource=admin because the root user is created in the admin database
        // by MONGO_INITDB_ROOT_USERNAME, not in the application database.
        var uri = $"mongodb://{Uri.EscapeDataString(credential.Username!)}:"
            + $"{Uri.EscapeDataString(credential.Password.Reveal())}@"
            + $"{endpoint.HostName}:{endpoint.Port}/{endpoint.DatabaseName}?authSource=admin";

        return new ConnectionDetails(
            endpoint.HostName, endpoint.Port, endpoint.DatabaseName,
            credential.Username, credential.Password, new Secret(uri));
    }

    public override IReadOnlyList<EnvironmentEntry> BuildInjectedEnvironment(
        string keyPrefix,
        ConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        return Entries(
            ($"{keyPrefix}_HOST", details.Host, false),
            ($"{keyPrefix}_PORT", details.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
            ($"{keyPrefix}_DATABASE", details.DatabaseName!, false),
            ($"{keyPrefix}_USER", details.Username!, false),
            ($"{keyPrefix}_PASSWORD", details.Password.Reveal(), true),
            ($"{keyPrefix}_URI", details.ConnectionString.Reveal(), true));
    }

    public override Task<BackupArtifact> BackupAsync(
        BackupOperation operation,
        Stream destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // --archive sends a single stream to stdout instead of writing a
        // directory tree, which is the only shape that can be piped out of an
        // exec and hashed as it goes.
        return LogicalBackup.RunAsync(
            Runtime,
            operation.Endpoint.ContainerId,
            [
                "mongodump", "--archive", "--gzip",
                "--username", operation.Credential.Username!,
                "--password", operation.Credential.Password.Reveal(),
                "--authenticationDatabase", "admin",
                "--db", operation.Endpoint.DatabaseName!,
            ],
            [],
            destination,
            operation.EngineSnapshot,
            operation.Progress,
            ct);
    }

    public override Task RestoreAsync(RestoreOperation operation, Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return LogicalBackup.RestoreAsync(
            Runtime,
            operation.Endpoint.ContainerId,
            [
                "mongorestore", "--gzip", "--drop",
                "--archive=/tmp/" + LogicalBackup.StagingFile,
                "--username", operation.Credential.Username!,
                "--password", operation.Credential.Password.Reveal(),
                "--authenticationDatabase", "admin",
            ],
            [],
            source,
            ct);
    }

    public override async Task RotatePasswordAsync(
        DatabaseEndpoint endpoint,
        DatabaseCredentialValue current,
        Secret replacement,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(current);

        var result = await Runtime.Containers.ExecAsync(
            new ExecRequest(
                endpoint.ContainerId,
                [
                    "mongosh", "--quiet",
                    "--username", current.Username!,
                    "--password", current.Password.Reveal(),
                    "--authenticationDatabase", "admin",
                    "admin",
                    "--eval",
                    $"db.changeUserPassword('{current.Username}', '{SafeLiteral(replacement)}')",
                ],
                []),
            null,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Password rotation failed: {result.StandardError}");
        }
    }
}

/// <summary>
/// Host port publishing.
/// </summary>
/// <remarks>
/// Shared so no engine can accidentally default to <c>0.0.0.0</c>. Publishing a
/// database to the public internet is an explicit, separately confirmed choice;
/// a default that did it would put unauthenticated-adjacent databases online
/// within a week of launch.
/// </remarks>
internal static class DatabasePorts
{
    public static IReadOnlyList<PortBinding> For(DatabaseProvisionSpec spec, int containerPort) =>
        spec.PublishedPort is null
            ? []
            : [new PortBinding(containerPort, spec.PublishedPort.Value, spec.PublishBindAddress)];
}
