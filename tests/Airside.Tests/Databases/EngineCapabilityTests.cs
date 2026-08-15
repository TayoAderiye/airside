using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Naming;
using Airside.Tests.Fakes;

namespace Airside.Tests.Databases;

/// <summary>
/// The engine abstraction, exercised through capabilities rather than by
/// switching on engine kind — which is what the calling code does too.
/// </summary>
public class EngineCapabilityTests
{
    private static IReadOnlyList<IDatabaseEngine> AllEngines() => EngineFactory.All();

    [Fact]
    public void EveryEngine_SupportsExactlyOneBackupStyle()
    {
        foreach (var engine in AllEngines())
        {
            // Not both, not neither. An engine claiming both would make the backup
            // scheduler's choice arbitrary; one claiming neither cannot be backed
            // up at all and should say so through a different capability.
            Assert.True(
                engine.Capabilities.SupportsLogicalBackup ^ engine.Capabilities.SupportsSnapshotBackup,
                $"{engine.Kind} must support logical XOR snapshot backup.");
        }
    }

    [Fact]
    public void OnlySnapshotEngines_RequireAStopForRestore()
    {
        foreach (var engine in AllEngines())
        {
            if (engine.Capabilities.RequiresStopForRestore)
            {
                Assert.True(engine.Capabilities.SupportsSnapshotBackup);
            }
        }
    }

    [Fact]
    public void Redis_DeclaresNoDatabaseNameAndNoUserAccounts()
    {
        var redis = EngineFactory.Redis();

        Assert.False(redis.Capabilities.SupportsDatabaseName);
        Assert.False(redis.Capabilities.SupportsUserAccounts);
        Assert.True(redis.Capabilities.RequiresMaxMemory);
        Assert.True(redis.Capabilities.RequiresStopForRestore);
        Assert.Equal(QueryDialect.RedisCommand, redis.Capabilities.QueryDialect);
        Assert.Equal("REDIS", redis.Capabilities.DefaultEnvKeyPrefix);
    }

    [Fact]
    public void SqlEngines_RequireANameAndAUser()
    {
        foreach (var engine in AllEngines().Where(e => e.Kind != DatabaseEngineKind.Redis))
        {
            Assert.True(engine.Capabilities.SupportsDatabaseName);
            Assert.True(engine.Capabilities.SupportsUserAccounts);
            Assert.False(engine.Capabilities.RequiresMaxMemory);
            Assert.Null(engine.Capabilities.EvictionPolicies);
        }
    }

    [Fact]
    public void Redis_RejectsADatabaseName()
    {
        // Rejected, not ignored: a caller sending this has misunderstood
        // something, and saying so beats silently dropping the value.
        var result = EngineFactory.Redis().Validate(Spec.Redis() with { DatabaseName = "orders" });

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFieldNotApplicable, result.Failure!.Code);
        Assert.Equal("databaseName", result.Failure.Metadata!["field"]);
    }

    [Fact]
    public void Redis_RejectsAUsername()
    {
        var result = EngineFactory.Redis().Validate(Spec.Redis() with { Username = "admin" });

        Assert.True(result.IsFailure);
        Assert.Equal("username", result.Failure!.Metadata!["field"]);
    }

    [Fact]
    public void Redis_RequiresMaxMemory()
    {
        var result = EngineFactory.Redis().Validate(Spec.Redis() with { MaxMemoryBytes = null });

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFieldRequired, result.Failure!.Code);
        Assert.Equal("maxMemoryBytes", result.Failure.Metadata!["field"]);
    }

    [Fact]
    public void Redis_RejectsMaxMemoryEqualToTheContainerLimit()
    {
        // Equal guarantees the kernel kills the container before Redis ever
        // evicts: maxmemory bounds the dataset, not buffers or fragmentation.
        var spec = Spec.Redis();
        var result = EngineFactory.Redis().Validate(spec with { MaxMemoryBytes = spec.MemoryBytes });

        Assert.True(result.IsFailure);
        Assert.Equal("maxMemoryBytes", result.Failure!.Metadata!["field"]);
    }

    [Fact]
    public void Redis_RejectsAnUnknownEvictionPolicy()
    {
        var result = EngineFactory.Redis().Validate(Spec.Redis() with { MaxMemoryPolicy = "allkeys-yolo" });

        Assert.True(result.IsFailure);
        Assert.NotNull(result.Failure!.Metadata!["supported"]);
    }

    [Fact]
    public void Postgres_RequiresADatabaseName()
    {
        var result = EngineFactory.Postgres().Validate(Spec.Postgres() with { DatabaseName = null });

        Assert.True(result.IsFailure);
        Assert.Equal("databaseName", result.Failure!.Metadata!["field"]);
    }

    [Fact]
    public void Postgres_RejectsMaxMemory()
    {
        var result = EngineFactory.Postgres().Validate(Spec.Postgres() with { MaxMemoryBytes = 1024 });

        Assert.True(result.IsFailure);
        Assert.Equal("maxMemoryBytes", result.Failure!.Metadata!["field"]);
    }

    [Fact]
    public void AnyEngine_RejectsAnUnsupportedVersion()
    {
        var result = EngineFactory.Postgres().Validate(Spec.Postgres() with { Version = "9.6" });

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationUnsupportedVersion, result.Failure!.Code);
    }
}

public class RedisMaxMemoryTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void Recommendation_WithPersistence_Is70Percent()
    {
        // Not 80%. Redis forks during BGSAVE and AOF rewrite, and copy-on-write
        // pushes peak memory toward twice the dataset on a write-heavy instance —
        // at 80% the cgroup OOM killer takes the container mid-backup, and the
        // restart reads as a mystery crash rather than a backup problem.
        var recommendation = EngineFactory.Redis().RecommendMaxMemory(4 * Gib, persistenceEnabled: true);

        Assert.NotNull(recommendation);
        Assert.Equal(0.70, recommendation.FractionOfLimit);
        Assert.Equal((long)(4 * Gib * 0.70), recommendation.Bytes);
        Assert.Contains("fork", recommendation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recommendation_WithoutPersistence_Is80Percent()
    {
        var recommendation = EngineFactory.Redis().RecommendMaxMemory(4 * Gib, persistenceEnabled: false);

        Assert.NotNull(recommendation);
        Assert.Equal(0.80, recommendation.FractionOfLimit);
    }

    [Fact]
    public void Recommendation_IsAlwaysBelowTheContainerLimit()
    {
        foreach (var persistence in new[] { true, false })
        {
            var recommendation = EngineFactory.Redis().RecommendMaxMemory(2 * Gib, persistence);
            Assert.True(recommendation!.Bytes < 2 * Gib);
        }
    }

    [Fact]
    public void SqlEngines_HaveNoMaxMemoryRecommendation()
    {
        Assert.Null(EngineFactory.Postgres().RecommendMaxMemory(4 * Gib, true));
        Assert.Null(EngineFactory.MySql().RecommendMaxMemory(4 * Gib, true));
        Assert.Null(EngineFactory.MongoDb().RecommendMaxMemory(4 * Gib, true));
    }
}

public class ContainerSpecGenerationTests
{
    private static ProvisionContext Context(string slugText)
    {
        Assert.True(Slug.TryCreate(slugText, out var slug));

        return new ProvisionContext(
            AirsideNames.DatabaseContainer(slug),
            AirsideNames.DatabaseNetwork(slug),
            AirsideNames.Volume(slug, "data"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AirsideLabels.Managed] = AirsideLabels.True,
                [AirsideLabels.WorkloadId] = Guid.CreateVersion7().ToString(),
            });
    }

    [Fact]
    public void UnpublishedDatabase_ExposesNoHostPort()
    {
        var spec = EngineFactory.Postgres()
            .BuildContainerSpec(Spec.Postgres() with { PublishedPort = null }, Context("orders"));

        Assert.Empty(spec.Ports);
    }

    [Fact]
    public void PublishedDatabase_DefaultsToLoopback()
    {
        var spec = EngineFactory.Postgres()
            .BuildContainerSpec(Spec.Postgres() with { PublishedPort = 5432 }, Context("orders"));

        var binding = Assert.Single(spec.Ports);
        Assert.Equal("127.0.0.1", binding.BindAddress);
    }

    [Fact]
    public void EveryEngine_AppliesTheDefaultHardening()
    {
        foreach (var (engine, spec) in EngineFactory.AllWithSpecs())
        {
            var container = engine.BuildContainerSpec(spec, Context("workload"));

            Assert.True(container.Security.NoNewPrivileges, $"{engine.Kind} must set no-new-privileges.");
            Assert.Contains("ALL", container.Security.DropCapabilities, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void EveryEngine_MountsOnlyNamedVolumes()
    {
        foreach (var (engine, spec) in EngineFactory.AllWithSpecs())
        {
            var container = engine.BuildContainerSpec(spec, Context("workload"));

            Assert.All(container.Mounts, m => Assert.StartsWith("airside-vol-", m.VolumeName, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EveryEngine_HasAHealthProbeThatIsAnArgumentVector()
    {
        foreach (var (engine, spec) in EngineFactory.AllWithSpecs())
        {
            var container = engine.BuildContainerSpec(spec, Context("workload"));

            Assert.NotNull(container.HealthProbe);
            Assert.NotEmpty(container.HealthProbe!.Command);

            // No shell anywhere. A probe routed through sh -c would be the one
            // place a crafted value could become a command.
            Assert.DoesNotContain(container.HealthProbe.Command, c =>
                c is "sh" or "bash" or "/bin/sh" or "/bin/bash" or "-c");
        }
    }

    [Fact]
    public void SqlEngines_PassCredentialsByEnvironmentNotArgv()
    {
        foreach (var (engine, spec) in EngineFactory.AllWithSpecs()
            .Where(x => x.Engine.Kind != DatabaseEngineKind.Redis))
        {
            var container = engine.BuildContainerSpec(spec, Context("workload"));

            Assert.DoesNotContain(container.Command ?? [], c =>
                c.Contains(spec.Password.Reveal(), StringComparison.Ordinal));

            Assert.Contains(container.Environment, e => e.IsSensitive);
        }
    }

    [Fact]
    public void EnvironmentValues_AreAlwaysSecrets()
    {
        foreach (var (engine, spec) in EngineFactory.AllWithSpecs())
        {
            var container = engine.BuildContainerSpec(spec, Context("workload"));

            // Every value, not just the sensitive ones, so serialising or logging
            // a ContainerSpec cannot leak a password by accident.
            Assert.All(container.Environment, e => Assert.Equal(Secret.Mask, e.Value.ToString()));
        }
    }

    [Fact]
    public void Redis_WithoutAof_DisablesRdbSavePoints()
    {
        var container = EngineFactory.Redis()
            .BuildContainerSpec(Spec.Redis() with { AofEnabled = false }, Context("cache"));

        var command = container.Command!;
        var saveIndex = command.ToList().IndexOf("--save");

        Assert.True(saveIndex >= 0, "A cache-role Redis must have save points disabled.");
        Assert.Equal(string.Empty, command[saveIndex + 1]);
    }

    [Fact]
    public void Redis_WithAof_KeepsPersistenceOn()
    {
        var container = EngineFactory.Redis()
            .BuildContainerSpec(Spec.Redis() with { AofEnabled = true }, Context("cache"));

        var command = container.Command!.ToList();

        Assert.Equal("yes", command[command.IndexOf("--appendonly") + 1]);
        Assert.DoesNotContain("--save", command);
    }
}

public class InjectedEnvironmentTests
{
    private static ConnectionDetails Details(string? name, string? user) => new(
        "airside-db-orders", 5432, name, user, new Secret("pw"), new Secret("url"));

    [Fact]
    public void Redis_InjectsNoNameAndNoUserKeys()
    {
        // The specific shape the brief calls for: REDIS_HOST, REDIS_PORT,
        // REDIS_PASSWORD, REDIS_URL — and emphatically not the DATABASE_* set.
        var keys = EngineFactory.Redis()
            .BuildInjectedEnvironment("REDIS", Details(null, null))
            .Select(e => e.Key)
            .ToList();

        Assert.Equal(["REDIS_HOST", "REDIS_PORT", "REDIS_PASSWORD", "REDIS_URL"], keys);
        Assert.DoesNotContain(keys, k => k.EndsWith("_NAME", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.EndsWith("_USER", StringComparison.Ordinal));
    }

    [Fact]
    public void Postgres_InjectsTheFullDatabaseSet()
    {
        var keys = EngineFactory.Postgres()
            .BuildInjectedEnvironment("DATABASE", Details("orders", "app"))
            .Select(e => e.Key)
            .ToList();

        Assert.Equal(
            ["DATABASE_HOST", "DATABASE_PORT", "DATABASE_NAME", "DATABASE_USER", "DATABASE_PASSWORD", "DATABASE_URL"],
            keys);
    }

    [Fact]
    public void MongoDb_InjectsAUriNotAUrl()
    {
        var keys = EngineFactory.MongoDb()
            .BuildInjectedEnvironment("MONGO", Details("orders", "root"))
            .Select(e => e.Key)
            .ToList();

        Assert.Contains("MONGO_URI", keys, StringComparer.Ordinal);
        Assert.Contains("MONGO_DATABASE", keys, StringComparer.Ordinal);
    }

    [Fact]
    public void EveryEngine_MarksPasswordAndUrlSensitive()
    {
        foreach (var engine in EngineFactory.All())
        {
            var caps = engine.Capabilities;
            var entries = engine.BuildInjectedEnvironment(
                caps.DefaultEnvKeyPrefix,
                Details(caps.SupportsDatabaseName ? "orders" : null, caps.SupportsUserAccounts ? "app" : null));

            foreach (var entry in entries.Where(e =>
                e.Key.EndsWith("PASSWORD", StringComparison.Ordinal)
                || e.Key.EndsWith("URL", StringComparison.Ordinal)
                || e.Key.EndsWith("URI", StringComparison.Ordinal)))
            {
                Assert.True(entry.IsSensitive, $"{entry.Key} must be flagged sensitive.");
            }
        }
    }

    [Fact]
    public void PasswordsWithReservedCharacters_AreEscapedInConnectionStrings()
    {
        // A generated password never contains these, but an admin-supplied one
        // can, and an unescaped '@' silently truncates the host.
        var details = EngineFactory.Postgres().BuildConnectionDetails(
            new DatabaseEndpoint("cid", "airside-db-orders", 5432, "orders"),
            new DatabaseCredentialValue("app", new Secret("p@ss:word/#1")));

        var url = details.ConnectionString.Reveal();

        Assert.Contains("p%40ss%3Aword%2F%231", url, StringComparison.Ordinal);
        Assert.EndsWith("@airside-db-orders:5432/orders", url, StringComparison.Ordinal);
    }
}
