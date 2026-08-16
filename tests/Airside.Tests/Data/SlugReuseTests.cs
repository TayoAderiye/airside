using Airside.Core.Workloads;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Airside.Tests.Data;

/// <summary>
/// That a workload slug can be reused once the workload holding it is deleted.
/// </summary>
/// <remarks>
/// <para>
/// The configuration said so in a comment — "unique among non-deleted rows only,
/// so a slug can be reused after a delete" — and then called
/// <c>HasFilter(null)</c>, which clears a filter rather than setting one. The
/// index therefore covered soft-deleted rows, and deleting an application and
/// creating another with the same name produced a raw
/// <c>DbUpdateException</c> from the database.
/// </para>
/// <para>
/// It surfaced as a 500 rather than a conflict because the API's own duplicate
/// check reads through the soft-delete query filter: it sees no clash, so it
/// cannot warn about one, and the constraint fires after the request has already
/// been accepted as valid.
/// </para>
/// <para>
/// Against a real SQLite database rather than the in-memory provider, because the
/// property under test is a partial index and the in-memory provider has no
/// indexes at all — it would pass with the bug still present.
/// </para>
/// </remarks>
public sealed class SlugReuseTests : IDisposable
{
    private static readonly Guid HostId = new("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AirsideDbContext> _options;

    public SlugReuseTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AirsideDbContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly("Airside.Data.Migrations.Sqlite"))
            .Options;

        using var db = NewContext();
        db.Database.Migrate();

        db.Hosts.Add(new Host
        {
            Id = HostId,
            Name = "probe",
            IsLocal = true,
            VolumeRoot = "/tmp",
        });

        db.SaveChanges();
    }

    [Fact]
    public async Task ASlugIsFreedWhenItsWorkloadIsDeleted()
    {
        await using var db = NewContext();

        var first = Application("test1");
        db.Applications.Add(first);
        await db.SaveChangesAsync();

        // Soft delete, which is what the delete job does: the row stays so audit
        // and deployment references do not dangle.
        first.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        db.Applications.Add(Application("test1"));

        // The whole bug in one line. This threw DbUpdateException wrapping
        // "duplicate key value violates unique constraint".
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Applications.CountAsync(a => a.Slug == "test1"));
    }

    [Fact]
    public async Task TwoLiveWorkloadsStillCannotShareASlug()
    {
        // The half that must not be lost. Making the index conditional is only
        // correct if it still refuses the case it exists for.
        await using var db = NewContext();

        db.Applications.Add(Application("test2"));
        await db.SaveChangesAsync();

        db.Applications.Add(Application("test2"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ASlugCanBeReusedRepeatedly()
    {
        // Several deleted rows share the slug at once. A partial index permits
        // that; a filter written as "one deleted row is allowed" would not, and
        // would fail on the second re-create rather than the first.
        await using var db = NewContext();

        for (var i = 0; i < 4; i++)
        {
            var app = Application("recycled");
            db.Applications.Add(app);
            await db.SaveChangesAsync();

            app.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        db.Applications.Add(Application("recycled"));
        await db.SaveChangesAsync();

        Assert.Equal(4, await db.Applications.IgnoreQueryFilters()
            .CountAsync(a => a.Slug == "recycled" && a.DeletedAt != null));
    }

    [Fact]
    public async Task ADatabaseAndAnApplicationStillCannotShareASlug()
    {
        // Both kinds live in one table and every Docker name derives from the
        // slug, so a database and an application sharing one would collide on the
        // container name even though nothing in the API compares them.
        await using var db = NewContext();

        db.Applications.Add(Application("shared"));
        await db.SaveChangesAsync();

        db.Databases.Add(new DatabaseInstance
        {
            Id = Guid.CreateVersion7(),
            HostId = HostId,
            Kind = WorkloadKind.Database,
            Slug = "shared",
            DisplayName = "shared",
            State = "running",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static Application Application(string slug) => new()
    {
        Id = Guid.CreateVersion7(),
        HostId = HostId,
        Kind = WorkloadKind.Application,
        Slug = slug,
        DisplayName = slug,
        State = "running",
    };

    private AirsideDbContext NewContext() => new(_options, TimeProvider.System);

    public void Dispose() => _connection.Dispose();
}
