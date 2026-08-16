using Airside.Api.Jobs;
using Airside.Core.Common;
using Airside.Core.Jobs;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Data.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airside.Tests.Jobs;

/// <summary>
/// That a job left mid-flight by a dead process actually unwinds on restart.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher is deliberately durable — jobs are rows, so a self-update that
/// restarts the API does not lose a provision in flight. The recovery half of
/// that promise had a hole: orphans were moved to <c>Compensating</c> and then
/// never claimed, because the claim query only looked for <c>Queued</c>.
/// </para>
/// <para>
/// The consequence was worse than a job that never finished. A <c>Compensating</c>
/// row counts as a busy workload, so the stuck job blocked every later job for
/// that workload permanently, and since the dispatcher is a single reader loop,
/// the queue behind it stopped moving as well. It surfaced as a deployment that
/// sat in <c>Queued</c> for ever with nothing in the logs.
/// </para>
/// </remarks>
public sealed class JobRecoveryTests : IDisposable
{
    private readonly string _path;
    private readonly DbContextOptions<AirsideDbContext> _options;

    /// <summary>
    /// A file database, not <c>:memory:</c>.
    /// </summary>
    /// <remarks>
    /// In-memory SQLite only shares data between contexts that share one
    /// connection, and this test runs a background dispatcher polling on its own
    /// thread while the assertions poll on another. Two DbContexts over a single
    /// connection is not safe, and it fails as
    /// "unable to delete/modify user-function due to active statements" — which
    /// says nothing about concurrency and appears only when the timing lands
    /// badly, so it passed here for weeks and failed on CI.
    ///
    /// A file lets every context open its own connection, which is what the
    /// production code does too.
    /// </remarks>
    public JobRecoveryTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"airside-jobs-{Guid.CreateVersion7():N}.db");

        _options = new DbContextOptionsBuilder<AirsideDbContext>()
            .UseSqlite($"Data Source={_path}", o => o.MigrationsAssembly("Airside.Data.Migrations.Sqlite"))
            .Options;

        using var db = NewContext();
        db.Database.Migrate();
    }

    private AirsideDbContext NewContext() => new(_options, TimeProvider.System);

    [Fact]
    public async Task AJobLeftCompensatingIsUnwoundAndStopsBlockingItsWorkload()
    {
        var workload = Guid.CreateVersion7();

        // The state a killed process leaves behind: one job mid-flight, and a
        // second queued for the same workload waiting its turn.
        await using (var seed = NewContext())
        {
            seed.Jobs.Add(NewJob(workload, JobStatus.Running, DateTime.UtcNow.AddMinutes(-5)));
            seed.Jobs.Add(NewJob(workload, JobStatus.Queued, DateTime.UtcNow));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var handler = new RecordingHandler();
        using var provider = BuildProvider(handler);

        var dispatcher = new JobDispatcherService(
            provider.GetRequiredService<JobSignal>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IJobProgressObserver>(),
            TimeProvider.System,
            NullLogger<JobDispatcherService>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await WaitForAsync(async () =>
        {
            await using var db = NewContext();
            return !await db.Jobs.AnyAsync(j =>
                j.Status == JobStatus.Queued || j.Status == JobStatus.Compensating || j.Status == JobStatus.Running);
        });
        await dispatcher.StopAsync(CancellationToken.None);

        await using var final = NewContext();
        var jobs = await final.Jobs.OrderBy(j => j.QueuedAt).ToListAsync(CancellationToken.None);

        // The orphan unwound rather than sitting in Compensating for ever.
        Assert.Equal(JobStatus.Compensated, jobs[0].Status);
        Assert.Equal(1, handler.Compensations);

        // And the workload was released, so the job behind it ran. This is the
        // half that matters operationally: the queue kept moving.
        Assert.Equal(JobStatus.Succeeded, jobs[1].Status);
        Assert.Equal(1, handler.Executions);
    }

    private static Job NewJob(Guid workload, JobStatus status, DateTime queuedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = RecordingHandler.Type,
        Status = status,
        WorkloadId = workload,
        QueuedAt = queuedAt,
        PayloadJson = "{}",
        IdempotencyKey = Guid.NewGuid().ToString(),
    };

    private ServiceProvider BuildProvider(IJobHandler handler)
    {
        var services = new ServiceCollection();

        services.AddDbContext<AirsideDbContext>(o =>
            o.UseSqlite($"Data Source={_path}", s => s.MigrationsAssembly("Airside.Data.Migrations.Sqlite")));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<JobSignal>();
        services.AddSingleton<IJobProgressObserver, SilentObserver>();
        services.AddSingleton<IJobHandlerRegistry>(new SingleHandlerRegistry(handler));

        return services.BuildServiceProvider();
    }

    /// <summary>Polls rather than sleeping a fixed interval, so the test is not timing-tuned.</summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("The dispatcher did not reach a settled state within 15 seconds.");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A leaked temp file is a nuisance, not a test failure.
        }
    }

    private sealed class RecordingHandler : IJobHandler
    {
        public const string Type = "test.job";

        private int _executions;
        private int _compensations;

        public string JobType => Type;

        public int Executions => _executions;

        public int Compensations => _compensations;

        public Task<Result> ExecuteAsync(IJobContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(Result.Ok());
        }

        public Task CompensateAsync(IJobContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _compensations);
            return Task.CompletedTask;
        }
    }

    private sealed class SingleHandlerRegistry(IJobHandler handler) : IJobHandlerRegistry
    {
        public IJobHandler? Find(string jobType) =>
            string.Equals(jobType, handler.JobType, StringComparison.Ordinal) ? handler : null;
    }

    private sealed class SilentObserver : IJobProgressObserver
    {
        public Task OnJobUpdatedAsync(Guid jobId, CancellationToken ct) => Task.CompletedTask;

        public Task OnStepAppendedAsync(Guid jobId, int sequence, string name, string? message, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
