using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Jobs;
using Airside.Core.Workloads;
using Airside.Runtime.Databases;
using Airside.Runtime.Jobs;
using Airside.Core.Hosting;
using Airside.Core.Security;
using Airside.Runtime.Docker;
using Airside.Runtime.Hosting;
using Airside.Runtime.Security;
using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime;

public sealed class DockerOptions
{
    public const string Section = "Airside:Docker";

    public string SocketUri { get; set; } = "unix:///var/run/docker.sock";

    /// <summary>
    /// An explicit Engine API version, or empty to let the daemon choose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty by default, and that is deliberate. Airside originally pinned 1.43;
    /// Docker 29 reports <c>MinAPIVersion 1.44</c> and rejects anything older with
    /// a bare <c>BadRequest</c>, so the pin made the control plane unable to talk
    /// to any current daemon. Pinning high fails the other way — Docker 20.10 tops
    /// out at 1.41 — so there is no single version that works everywhere.
    /// </para>
    /// <para>
    /// Sending no version leaves the daemon to use its own, which works across the
    /// whole supported range because the Engine API keeps existing fields
    /// backward-compatible. The setting remains as an escape hatch for pinning
    /// against a specific host.
    /// </para>
    /// </remarks>
    public string ApiVersion { get; set; } = string.Empty;
}

public static class DependencyInjection
{
    public static IServiceCollection AddAirsideRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDockerClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DockerOptions>>().Value;

            var configuration = new DockerClientConfiguration(
                new Uri(options.SocketUri),
                credentials: null,
                defaultTimeout: TimeSpan.FromMinutes(5));

            return string.IsNullOrWhiteSpace(options.ApiVersion)
                ? configuration.CreateClient()
                : configuration.CreateClient(Version.Parse(options.ApiVersion));
        });

        services.AddSingleton<IContainerRuntime>(sp => new DockerContainerRuntime(
            sp.GetRequiredService<IDockerClient>(),
            sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<IHostResourceReader, HostResourceReader>();
        services.AddSingleton<IAllocationPolicy, StrictNoOvercommitPolicy>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<ISecretGenerator, SecretGenerator>();

        // Engines are registered explicitly, not scanned. A contributor adding a
        // fifth engine should have to say so here, because the registration is
        // also where its capabilities become visible to the whole system.
        services.AddSingleton<IDatabaseEngine, PostgresEngine>();
        services.AddSingleton<IDatabaseEngine, MySqlEngine>();
        services.AddSingleton<IDatabaseEngine, MongoDbEngine>();
        services.AddSingleton<IDatabaseEngine, RedisEngine>();
        services.AddSingleton<IDatabaseEngineRegistry, DatabaseEngineRegistry>();

        services.AddSingleton<Queries.IQueryConsoleFactory, Queries.QueryConsoleFactory>();

        services.AddScoped<IJobHandler, DatabaseProvisionHandler>();
        services.AddSingleton<BackupExecutor>();
        services.AddScoped<IJobHandler, DatabaseBackupHandler>();
        services.AddScoped<IJobHandler, DatabaseRestoreHandler>();
        services.AddScoped<IJobHandler, RotateCredentialsHandler>();
        services.AddScoped<IJobHandler, DatabaseResizeHandler>();
        services.AddScoped<IJobHandler, DatabaseDeleteHandler>();

        // One class, three job types. The only difference is which container call
        // it makes and what state it lands in.
        services.AddScoped<IJobHandler>(sp => new DatabaseLifecycleHandler(
            sp.GetRequiredService<IContainerRuntime>(),
            sp.GetRequiredService<IDatabaseWorkloadStore>(),
            DatabaseJobTypes.Start,
            DatabaseState.Running));
        services.AddScoped<IJobHandler>(sp => new DatabaseLifecycleHandler(
            sp.GetRequiredService<IContainerRuntime>(),
            sp.GetRequiredService<IDatabaseWorkloadStore>(),
            DatabaseJobTypes.Stop,
            DatabaseState.Stopped));
        services.AddScoped<IJobHandler>(sp => new DatabaseLifecycleHandler(
            sp.GetRequiredService<IContainerRuntime>(),
            sp.GetRequiredService<IDatabaseWorkloadStore>(),
            DatabaseJobTypes.Restart,
            DatabaseState.Running));

        return services;
    }
}
