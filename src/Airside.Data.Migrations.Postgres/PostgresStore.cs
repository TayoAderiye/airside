using Airside.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Airside.Data.Migrations.Postgres;

public static class PostgresStore
{
    public const string MigrationsAssembly = "Airside.Data.Migrations.Postgres";

    public static IServiceCollection AddPostgresStore(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<AirsideDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(MigrationsAssembly);
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(5), null);
            }));

        return services;
    }
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API.
/// </summary>
/// <remarks>
/// The connection string here is never used to connect — <c>migrations add</c>
/// only needs a provider to know which DDL to emit. Keeping the factory in the
/// migrations project means a contributor can generate migrations without a
/// running database or a configured host.
/// </remarks>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<AirsideDbContext>
{
    public AirsideDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AirsideDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=airside;Username=airside;Password=design-time-only",
                npgsql => npgsql.MigrationsAssembly(PostgresStore.MigrationsAssembly))
            .Options;

        return new AirsideDbContext(options, TimeProvider.System);
    }
}
