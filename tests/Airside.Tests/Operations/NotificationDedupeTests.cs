using Airside.Api.Features.Operations;
using Airside.Core.Operations;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airside.Tests.Operations;

/// <summary>
/// That repeat observations of one condition collapse into one notification.
/// </summary>
/// <remarks>
/// The conditions worth notifying about are found by sweeps on timers, so each one
/// is observed again every few hours for as long as it holds. A certificate
/// expiring in a week is one fact; appending it four times a day produces
/// twenty-eight rows before the certificate expires and a list nobody reads — at
/// which point the notification that mattered is lost in it.
/// </remarks>
public sealed class NotificationDedupeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AirsideDbContext> _options;

    public NotificationDedupeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AirsideDbContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly("Airside.Data.Migrations.Sqlite"))
            .Options;

        using var db = NewContext();
        db.Database.Migrate();
    }

    private AirsideDbContext NewContext() => new(_options, TimeProvider.System);

    private static Notifier NewNotifier(AirsideDbContext db) =>
        new(db, TimeProvider.System, NullLogger<Notifier>.Instance);

    [Fact]
    public async Task RepeatObservationsUpdateOneRowRatherThanAppending()
    {
        await using var db = NewContext();
        var notifier = NewNotifier(db);

        var key = NotificationKeys.CertificateExpiring("app.example.com");

        for (var i = 0; i < 4; i++)
        {
            await notifier.RaiseAsync(
                new NotificationRequest(key, NotificationLevel.Warning, "Certificate expiring", $"{14 - i} days"),
                CancellationToken.None);
        }

        var notification = Assert.Single(await db.Notifications.ToListAsync(CancellationToken.None));

        Assert.Equal(4, notification.OccurrenceCount);

        // The body is the latest, not the first. The same condition changes degree
        // as the date approaches, and showing the stalest wording would tell the
        // operator they have longer than they do.
        Assert.Equal("11 days", notification.Body);
    }

    [Fact]
    public async Task ADifferentConditionIsItsOwnNotification()
    {
        await using var db = NewContext();
        var notifier = NewNotifier(db);

        await notifier.RaiseAsync(
            new NotificationRequest(
                NotificationKeys.CertificateExpiring("a.example.com"), NotificationLevel.Warning, "a", "a"),
            CancellationToken.None);

        await notifier.RaiseAsync(
            new NotificationRequest(
                NotificationKeys.CertificateExpiring("b.example.com"), NotificationLevel.Warning, "b", "b"),
            CancellationToken.None);

        Assert.Equal(2, await db.Notifications.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResolvingLeavesTheRecordAndAllowsTheConditionToRecur()
    {
        await using var db = NewContext();
        var notifier = NewNotifier(db);

        var key = NotificationKeys.DomainFailed("app.example.com");

        await notifier.RaiseAsync(
            new NotificationRequest(key, NotificationLevel.Error, "Failed", "one"), CancellationToken.None);

        await notifier.ResolveAsync(key, CancellationToken.None);

        // Resolved rather than deleted, so looking back distinguishes "this broke
        // and recovered" from "this never happened".
        var resolved = Assert.Single(await db.Notifications.ToListAsync(CancellationToken.None));
        Assert.NotNull(resolved.ResolvedAt);

        // And the same condition happening again is a new notification rather than
        // a silent update to a closed one.
        await notifier.RaiseAsync(
            new NotificationRequest(key, NotificationLevel.Error, "Failed", "two"), CancellationToken.None);

        Assert.Equal(2, await db.Notifications.CountAsync(CancellationToken.None));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.ResolvedAt == null, CancellationToken.None));
    }

    [Fact]
    public async Task AConditionThatWorsensUnacknowledgesItself()
    {
        // Somebody dismissed it while it was a warning. Escalating to an error is
        // new information, and leaving it acknowledged would hide it for good.
        await using var db = NewContext();
        var notifier = NewNotifier(db);

        var key = NotificationKeys.CertificateExpiring("app.example.com");

        await notifier.RaiseAsync(
            new NotificationRequest(key, NotificationLevel.Warning, "Expiring", "14 days"), CancellationToken.None);

        var raised = await db.Notifications.FirstAsync(CancellationToken.None);
        raised.AcknowledgedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        await notifier.RaiseAsync(
            new NotificationRequest(key, NotificationLevel.Error, "Expired", "today"), CancellationToken.None);

        var escalated = await db.Notifications.FirstAsync(CancellationToken.None);

        Assert.Equal(NotificationSeverity.Error, escalated.Severity);
        Assert.Null(escalated.AcknowledgedAt);
    }

    public void Dispose() => _connection.Dispose();
}
