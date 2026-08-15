using Airside.Core.Audit;
using Airside.Core.Jobs;
using Airside.Data.Audit;
using Airside.Data.Jobs;
using Airside.Data.Seeding;
using Microsoft.Extensions.DependencyInjection;

namespace Airside.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Registers everything in the data layer except the database provider, which
    /// the composition root selects from configuration — see
    /// <c>Airside.Data.Migrations.Postgres</c> and <c>…Sqlite</c>.
    /// </summary>
    /// <remarks>
    /// Registration is explicit rather than assembly-scanned. A contributor should
    /// be able to find the implementation of an interface by reading one file
    /// instead of inferring a naming convention, and explicit registration also
    /// survives trimming.
    /// </remarks>
    public static IServiceCollection AddAirsideData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IJobQueue, JobQueue>();
        services.AddScoped<DbSeeder>();

        return services;
    }
}
