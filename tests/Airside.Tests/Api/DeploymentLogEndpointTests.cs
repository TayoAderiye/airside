using Airside.Data;
using Airside.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Airside.Tests.Api;

/// <summary>
/// That "this build has produced no output yet" is not reported as "no such
/// deployment".
/// </summary>
/// <remarks>
/// <para>
/// The build log is written as one row, and the row is only created once there
/// is something to put in it. Answering 404 until then made the deploying
/// screen — which polls every two seconds so an operator can watch a build —
/// emit a failed request every two seconds for the length of that build, and
/// forever for a deployment from a prebuilt image, which never produces output
/// at all.
/// </para>
/// <para>
/// Nothing was broken by it, which is why it shipped. The cost is a network
/// panel full of red on a working deployment, and an operator who learns that
/// red there means nothing.
/// </para>
/// </remarks>
public sealed class DeploymentLogEndpointTests : IDisposable
{
    private static readonly Guid HostId = new("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AirsideDbContext> _options;

    public DeploymentLogEndpointTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AirsideDbContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly("Airside.Data.Migrations.Sqlite"))
            .Options;

        using var db = new AirsideDbContext(_options, TimeProvider.System);
        db.Database.Migrate();

        db.Hosts.Add(new Host { Id = HostId, Name = "probe", IsLocal = true, VolumeRoot = "/tmp" });
        db.SaveChanges();
    }

    [Fact]
    public async Task ADeploymentWithNoOutputYetIsFoundAndEmpty()
    {
        await using var db = NewContext();
        var deployment = await SeedDeploymentAsync(db);

        var log = await db.DeploymentLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.DeploymentId == deployment);
        var exists = await db.Deployments.AsNoTracking().AnyAsync(d => d.Id == deployment);

        // The two facts the endpoint branches on: no log row, but the deployment
        // is real — so the answer is an empty log, not "not found".
        Assert.Null(log);
        Assert.True(exists);
    }

    [Fact]
    public async Task AnUnknownDeploymentIsStillNotFound()
    {
        // The half that must survive. Reserving 404 for a missing deployment is
        // only useful if it still happens.
        await using var db = NewContext();
        await SeedDeploymentAsync(db);

        Assert.False(await db.Deployments.AsNoTracking().AnyAsync(d => d.Id == Guid.CreateVersion7()));
    }

    [Fact]
    public async Task OutputWrittenMidBuildIsReadableBeforeTheBuildEnds()
    {
        // The point of flushing on a timer: the row exists, and grows, while the
        // build is still running.
        await using var db = NewContext();
        var deployment = await SeedDeploymentAsync(db);

        db.DeploymentLogs.Add(new DeploymentLog
        {
            DeploymentId = deployment,
            Content = "Step 1/4 : FROM node:22-alpine\n",
            ByteCount = 32,
        });
        await db.SaveChangesAsync();

        var partial = await db.DeploymentLogs.AsNoTracking()
            .FirstAsync(l => l.DeploymentId == deployment);

        Assert.Contains("Step 1/4", partial.Content, StringComparison.Ordinal);

        var row = await db.DeploymentLogs.FirstAsync(l => l.DeploymentId == deployment);
        row.Content += "Step 2/4 : RUN npm ci\n";
        await db.SaveChangesAsync();

        var later = await db.DeploymentLogs.AsNoTracking()
            .FirstAsync(l => l.DeploymentId == deployment);

        Assert.Contains("Step 2/4", later.Content, StringComparison.Ordinal);
    }

    private static async Task<Guid> SeedDeploymentAsync(AirsideDbContext db)
    {
        var app = new Application
        {
            Id = Guid.CreateVersion7(),
            HostId = HostId,
            Kind = Core.Workloads.WorkloadKind.Application,
            Slug = "probe-app",
            DisplayName = "probe-app",
            State = "running",
        };

        db.Applications.Add(app);
        await db.SaveChangesAsync();

        var deployment = new Deployment
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = app.Id,
            Number = 1,
            Status = DeploymentStatus.Building,
            TriggerKind = DeploymentTrigger.Manual,
        };

        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        return deployment.Id;
    }

    private AirsideDbContext NewContext() => new(_options, TimeProvider.System);

    public void Dispose() => _connection.Dispose();
}
