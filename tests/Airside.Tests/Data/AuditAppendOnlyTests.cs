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
