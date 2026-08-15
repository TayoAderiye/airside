using Airside.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Airside.Data.Migrations.Sqlite;

public static class SqliteStore
{
    public const string MigrationsAssembly = "Airside.Data.Migrations.Sqlite";

    public static IServiceCollection AddSqliteStore(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<AirsideDbContext>(options =>
            options.UseSqlite(connectionString, sqlite =>
                sqlite.MigrationsAssembly(MigrationsAssembly)));

        return services;
    }
}

/// <summary>Design-time factory. See the Postgres equivalent for the reasoning.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<AirsideDbContext>
{
    public AirsideDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AirsideDbContext>()
            .UseSqlite(
                "Data Source=design-time-only.db",
                sqlite => sqlite.MigrationsAssembly(SqliteStore.MigrationsAssembly))
            .Options;

        return new AirsideDbContext(options, TimeProvider.System);
    }
}
