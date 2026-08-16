using Airside.Core.Audit;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Airside.Tests.Data;

/// <summary>
/// Append-only audit, verified at both layers.
/// </summary>
/// <remarks>
/// The application check and the database trigger are tested separately on
/// purpose: the in-code guard is what produces a sensible error, and the trigger
/// is what still holds when someone reaches the database with raw SQL. A test
/// that only exercised one would let the other rot.
/// </remarks>
public class AuditAppendOnlyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AirsideDbContext> _options;

    public AuditAppendOnlyTests()
    {
        // A real SQLite database rather than the in-memory provider: the guard
        // under test is a trigger, and the in-memory provider has no triggers.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AirsideDbContext>()
            // Migrations live in a separate assembly per provider, so the
            // assembly has to be named explicitly here too — without it EF looks
            // in Airside.Data, finds nothing, and creates no schema at all.
            .UseSqlite(_connection, o => o.MigrationsAssembly("Airside.Data.Migrations.Sqlite"))
            .Options;

        using var db = NewContext();
        db.Database.Migrate();
    }

    private AirsideDbContext NewContext() => new(_options, TimeProvider.System);

    [Fact]
    public async Task WriteAsync_AppendsEvent()
    {
        await using var db = NewContext();
        var writer = new AuditWriterProbe(db);

        await writer.WriteAsync(new AuditEntry
        {
            Action = AuditActions.DatabaseDeleted,
            Result = AuditResult.Success,
            UserEmailSnapshot = "tayo@example.com",
        }, CancellationToken.None);

        Assert.Equal(1, await db.AuditEvents.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveChanges_ModifyingAnAuditEvent_IsRejectedByTheApplication()
    {
        await using var db = NewContext();
        db.AuditEvents.Add(NewEvent());
        await db.SaveChangesAsync(CancellationToken.None);

        var stored = await db.AuditEvents.FirstAsync(CancellationToken.None);
        stored.Action = "tampered";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(CancellationToken.None));

        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChanges_DeletingAnAuditEvent_IsRejectedByTheApplication()
    {
        await using var db = NewContext();
        db.AuditEvents.Add(NewEvent());
        await db.SaveChangesAsync(CancellationToken.None);

        db.AuditEvents.Remove(await db.AuditEvents.FirstAsync(CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RawSqlUpdate_IsRejectedByTheDatabaseTrigger()
    {
        await using var db = NewContext();
        db.AuditEvents.Add(NewEvent());
        await db.SaveChangesAsync(CancellationToken.None);

        // Bypasses every line of C# in the product. This is the guarantee that
        // still holds when someone opens a SQL console.
        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE audit_events SET \"Action\" = 'tampered'",
                CancellationToken.None));

        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawSqlDelete_IsRejectedByTheDatabaseTrigger()
    {
        await using var db = NewContext();
        db.AuditEvents.Add(NewEvent());
        await db.SaveChangesAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "DELETE FROM audit_events",
                CancellationToken.None));

        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AuditEvent NewEvent() => new()
    {
        OccurredAt = DateTime.UtcNow,
        Action = AuditActions.SecretRevealed,
        Result = AuditResult.Success,
    };

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The writer is internal to Airside.Data; this reproduces its one behaviour.</summary>
    private sealed class AuditWriterProbe(AirsideDbContext db)
    {
        public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                OccurredAt = DateTime.UtcNow,
                Action = entry.Action,
                Result = entry.Result,
                UserEmailSnapshot = entry.UserEmailSnapshot,
            });

            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>
/// The image-variant backstop in <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// The service layer produces the actionable error; this is what still holds if a
/// future endpoint forgets to ask. It covers EF only — a raw SQL UPDATE bypasses
/// it, unlike the audit trigger, because variant immutability was specified as a
/// service-layer rule rather than a database one.
/// </remarks>
public class ImageVariantImmutabilityInDbTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AirsideDbContext> _options;

    public ImageVariantImmutabilityInDbTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AirsideDbContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly("Airside.Data.Migrations.Sqlite"))
            .Options;

        using var db = New();
        db.Database.Migrate();
    }

    private AirsideDbContext New() => new(_options, TimeProvider.System);

    private async Task<Guid> SeedAsync(Airside.Core.Databases.ImageVariant variant)
    {
        await using var db = New();

        var host = new Airside.Data.Entities.Host { Name = "local" };
        db.Hosts.Add(host);

        var database = new DatabaseInstance
        {
            HostId = host.Id,
            Kind = Airside.Core.Workloads.WorkloadKind.Database,
            Slug = "orders",
            DisplayName = "Orders",
            State = "Running",
            Engine = Airside.Core.Databases.DatabaseEngineKind.Postgres,
            Version = "16",
            ImageVariant = variant,
            ImageRef = "postgres:16-alpine",
        };

        db.Databases.Add(database);
        await db.SaveChangesAsync(CancellationToken.None);

        return database.Id;
    }

    [Fact]
    public async Task ChangingTheVariantOnASavedRowIsRejected()
    {
        var id = await SeedAsync(Airside.Core.Databases.ImageVariant.Alpine);

        await using var db = New();
        var database = await db.Databases.FirstAsync(d => d.Id == id, CancellationToken.None);
        database.ImageVariant = Airside.Core.Databases.ImageVariant.Debian;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(CancellationToken.None));

        Assert.Contains("fixed at provisioning", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FlippingUsesCustomImageIsAlsoRejected()
    {
        var id = await SeedAsync(Airside.Core.Databases.ImageVariant.Alpine);

        await using var db = New();
        var database = await db.Databases.FirstAsync(d => d.Id == id, CancellationToken.None);
        database.UsesCustomImage = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OtherFieldsRemainEditable()
    {
        // The guard must not turn the whole row read-only: resizing and state
        // changes go through the same SaveChanges.
        var id = await SeedAsync(Airside.Core.Databases.ImageVariant.Alpine);

        await using var db = New();
        var database = await db.Databases.FirstAsync(d => d.Id == id, CancellationToken.None);
        database.MemoryLimitBytes = 1024;
        database.State = "Stopped";

        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(1024, (await db.Databases.FirstAsync(d => d.Id == id, CancellationToken.None)).MemoryLimitBytes);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
