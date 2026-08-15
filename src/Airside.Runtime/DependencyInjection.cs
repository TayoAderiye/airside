using Airside.Core.Containers;
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
    /// The Engine API version to negotiate. Pinned rather than left to
    /// Docker.DotNet's default, because the library has had no release since 2021
    /// and its built-in default predates several daemon versions Airside will meet.
    /// </summary>
    public string ApiVersion { get; set; } = "1.43";
}

public static class DependencyInjection
{
    public static IServiceCollection AddAirsideRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDockerClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DockerOptions>>().Value;

            return new DockerClientConfiguration(
                new Uri(options.SocketUri),
                credentials: null,
                defaultTimeout: TimeSpan.FromMinutes(5))
                .CreateClient(Version.Parse(options.ApiVersion));
        });

        services.AddSingleton<IContainerRuntime>(sp => new DockerContainerRuntime(
            sp.GetRequiredService<IDockerClient>(),
            sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<IHostResourceReader, HostResourceReader>();
        services.AddSingleton<IAllocationPolicy, StrictNoOvercommitPolicy>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<ISecretGenerator, SecretGenerator>();

        return services;
    }
}
