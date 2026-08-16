using Airside.Api.Features.Operations;
using Airside.Core.Containers;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airside.Tests.Operations;

/// <summary>
/// The system backup, taken and read back.
/// </summary>
/// <remarks>
/// The property under test is not "a file was produced" but "the file contains
/// both halves". A control-plane backup without the Data Protection key ring
/// restores into an instance that cannot decrypt a single stored password, and
/// the operator discovers that only when something needs one — so a backup
/// missing it has to be detectable before a restore, not after.
/// </remarks>
public sealed class SystemBackupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _root;
    private readonly ServiceProvider _provider;

    public SystemBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"airside-backup-test-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(_root);

        var databasePath = Path.Combine(_root, "airside.db");
        var connectionString = $"Data Source={databasePath}";

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AirsideDbContext>(o =>
            o.UseSqlite(connectionString, s => s.MigrationsAssembly("Airside.Data.Migrations.Sqlite")));
        services.AddSingleton(TimeProvider.System);

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();
        db.Database.Migrate();
    }

    private SystemBackupProvider Build(bool withKeyRing)
    {
        var keyRing = Path.Combine(_root, "keys");

        if (withKeyRing)
        {
            Directory.CreateDirectory(keyRing);
            File.WriteAllText(Path.Combine(keyRing, "key-abc.xml"), "<key id=\"abc\" />");
        }

        return new SystemBackupProvider(
            new FakeContainerRuntime(),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new SystemBackupContext(
                "Sqlite",
                $"Data Source={Path.Combine(_root, "airside.db")}",
                keyRing,
                "airside-db",
                "airside",
                "airside"),
            TimeProvider.System,
            NullLogger<SystemBackupProvider>.Instance);
    }

    [Fact]
    public async Task ABackupContainsTheStoreAndTheKeyRingAndVerifies()
    {
        var provider = Build(withKeyRing: true);

        var result = await provider.CreateAsync(Path.Combine(_root, "out"), CancellationToken.None);

        Assert.True(File.Exists(result.ArchivePath));
        Assert.True(result.SizeBytes > 0);
        Assert.Equal(64, result.Sha256.Length);

        var verification = await provider.VerifyAsync(result.ArchivePath, CancellationToken.None);

        Assert.True(verification.IsUsable);
        Assert.True(verification.KeyRingIncluded);
        Assert.Equal("Sqlite", verification.StoreProvider);
        Assert.Null(verification.Detail);
    }

    [Fact]
    public async Task ABackupWithoutAKeyRingIsUsableButSaysWhatItCannotDo()
    {
        // Not a refusal — the schema and data are still worth having. But
        // restoring it silently would produce an instance whose every stored
        // secret is undecryptable, so the gap is named rather than left to be
        // discovered.
        var provider = Build(withKeyRing: false);

        var result = await provider.CreateAsync(Path.Combine(_root, "out-nokeys"), CancellationToken.None);
        var verification = await provider.VerifyAsync(result.ArchivePath, CancellationToken.None);

        Assert.True(verification.IsUsable);
        Assert.False(verification.KeyRingIncluded);
        Assert.Contains("cannot decrypt", verification.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSnapshotIsConsistentRatherThanACopyOfALiveFile()
    {
        // VACUUM INTO rather than File.Copy. A copy taken while the API holds the
        // database open captures it mid-write and, in WAL mode, misses everything
        // still in the log — so the restored database is missing the most recent
        // writes with nothing to indicate it.
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

            db.Notifications.Add(new Notification
            {
                Id = Guid.CreateVersion7(),
                DedupeKey = "test:written-just-now",
                Title = "t",
                Body = "b",
                FirstSeenAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            });

            await db.SaveChangesAsync(TestContext());
        }

        var result = await Build(withKeyRing: true).CreateAsync(Path.Combine(_root, "out2"), CancellationToken.None);

        // Extract the dump and open it: the row written moments before the backup
        // has to be in it.
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        System.Formats.Tar.TarFile.ExtractToDirectory(result.ArchivePath, extracted, overwriteFiles: true);

        var dump = Path.Combine(extracted, "store.dump");
        Assert.True(File.Exists(dump));

        await using var restored = new SqliteConnection($"Data Source={dump}");
        await restored.OpenAsync(CancellationToken.None);

        await using var command = restored.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notifications WHERE DedupeKey = 'test:written-just-now'";

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task AnArchiveThatIsNotAnAirsideBackupIsRejected()
    {
        var stray = Path.Combine(_root, "stray.tar");
        await File.WriteAllTextAsync(stray, "this is not a tar archive", CancellationToken.None);

        var verification = await Build(withKeyRing: true).VerifyAsync(stray, CancellationToken.None);

        Assert.False(verification.IsUsable);
    }

    private static CancellationToken TestContext() => CancellationToken.None;

    public void Dispose()
    {
        _connection.Dispose();
        _provider.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is a nuisance, not a test failure.
        }
    }
}
