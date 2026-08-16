using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Jobs;
using Airside.Core.Workloads;
using Airside.Runtime.Applications;
using Airside.Runtime.Databases;
using Airside.Runtime.Jobs;
using Airside.Core.Hosting;
using Airside.Core.Security;
using Airside.Runtime.Docker;
using Airside.Core.Proxy;
using Airside.Runtime.Hosting;
using Airside.Core.Domains;
using Airside.Runtime.Dns;
using Airside.Runtime.Domains;
using Airside.Runtime.Proxy;
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
        services.AddSingleton<GitSource>();

        // A typed client so the admin address is configured once. The timeout is
        // short: a proxy that is not answering should fail a deploy quickly rather
        // than hold the job dispatcher, which runs one job at a time.
        services.AddHttpClient<IProxyManager, CaddyProxyManager>((sp, client) =>
        {
            var proxy = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CaddyOptions>>().Value;
            client.BaseAddress = new Uri(proxy.AdminAddress);
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<EnvironmentRenderer>();

        // Pre-flight and certificate validation. The suffix list and the proxy
        // range index each parse an embedded file once, so they are singletons;
        // the locks must be, or two jobs would take different semaphores for the
        // same hostname and serialise nothing.
        services.AddSingleton<IPublicSuffixList, PublicSuffixList>();
        services.AddSingleton<ProxyRangeIndex>();
        services.AddSingleton<HostnameLocks>();
        services.AddSingleton<ICertificateValidator, CertificateValidator>();
        services.AddSingleton<IDnsInspector, DnsInspector>();
        services.AddHttpClient<IExternalReachability, ExternalReachability>();
        services.AddScoped<IDomainPreflight, DomainPreflight>();

        services.AddScoped<IJobHandler, DeployHandler>();
        services.AddScoped<IJobHandler, AttachmentHandler>();
        // One handler class, three job types. Registering them separately keeps
        // start, stop, and restart from drifting apart in behaviour.
        services.AddScoped<IJobHandler>(sp => ActivatorUtilities.CreateInstance<ApplicationLifecycleHandler>(
            sp, ApplicationJobTypes.Start, ApplicationState.Running));
        services.AddScoped<IJobHandler>(sp => ActivatorUtilities.CreateInstance<ApplicationLifecycleHandler>(
            sp, ApplicationJobTypes.Stop, ApplicationState.Stopped));
        services.AddScoped<IJobHandler>(sp => ActivatorUtilities.CreateInstance<ApplicationLifecycleHandler>(
            sp, ApplicationJobTypes.Restart, ApplicationState.Running));
        services.AddScoped<IJobHandler, ApplicationDeleteHandler>();
        services.AddScoped<IJobHandler, BindDomainHandler>();
        services.AddScoped<IJobHandler, UnbindDomainHandler>();
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
