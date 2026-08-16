using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Airside.Core.Containers;
using Airside.Core.Naming;
using Airside.Core.Operations;
using Airside.Data;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Operations;

/// <param name="StoreProvider"><c>Postgres</c> or <c>Sqlite</c>.</param>
/// <param name="ConnectionString">Used only for the SQLite path, to locate the file.</param>
public sealed record SystemBackupContext(
    string StoreProvider,
    string ConnectionString,
    string KeyRingPath,
    string StoreContainerName,
    string StoreDatabase,
    string StoreUser);

/// <summary>
/// Backs up the control-plane store and the Data Protection key ring together.
/// </summary>
/// <remarks>
/// <para>
/// One archive containing both, and that is the whole design. The key ring is what
/// decrypts every stored credential — database passwords, certificate private
/// keys, registry logins — so a database restored without it is a list of secrets
/// nobody can read, and the operator finds that out only when something needs one.
/// </para>
/// <para>
/// Putting them in separate files invites separate retention policies, and the
/// one that gets dropped is always the small unfamiliar one.
/// </para>
/// </remarks>
public sealed class SystemBackupProvider(
    IContainerRuntime runtime,
    IServiceScopeFactory scopeFactory,
    SystemBackupContext context,
    TimeProvider timeProvider,
    ILogger<SystemBackupProvider> logger) : ISystemBackupProvider
{
    private const string ManifestEntry = "airside-backup.json";
    private const string StoreEntry = "store.dump";
    private const string KeyRingPrefix = "keyring/";

    public async Task<SystemBackupResult> CreateAsync(string destinationDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDirectory);

        var timestamp = timeProvider.GetUtcNow();
        var name = $"airside-system-{timestamp:yyyyMMdd-HHmmss}.tar";
        var archivePath = Path.Combine(destinationDirectory, name);

        var staging = Path.Combine(Path.GetTempPath(), $"airside-sysbackup-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(staging);

        try
        {
            var dumpPath = Path.Combine(staging, StoreEntry);

            if (string.Equals(context.StoreProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await DumpSqliteAsync(dumpPath, ct).ConfigureAwait(false);
            }
            else
            {
                await DumpPostgresAsync(dumpPath, ct).ConfigureAwait(false);
            }

            CopyKeyRing(staging);
            WriteManifest(staging, timestamp);

            // Tar rather than zip: the key ring is a directory of XML files whose
            // permissions matter, and tar is what every restore tool on a Linux
            // host already understands.
            TarFile.CreateFromDirectory(staging, archivePath, includeBaseDirectory: false);

            var info = new FileInfo(archivePath);
            var hash = await ComputeSha256Async(archivePath, ct).ConfigureAwait(false);

            logger.LogInformation(
                "System backup written to {Path} ({Bytes} bytes)", archivePath, info.Length);

            return new SystemBackupResult(archivePath, info.Length, hash, context.StoreProvider, timestamp);
        }
        finally
        {
            // The staging directory holds a decrypted key ring copy, so it goes
            // whether or not the archive was written.
            Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// <c>VACUUM INTO</c>, not a file copy.
    /// </summary>
    /// <remarks>
    /// Copying the file while the API holds it open captures a database mid-write,
    /// and in WAL mode misses everything still in the write-ahead log. VACUUM INTO
    /// produces a consistent snapshot from inside the engine, which is the only
    /// way to get one without stopping the process doing the backup.
    /// </remarks>
    private async Task DumpSqliteAsync(string destination, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();

        // Parameterised: the destination is a path this process built, but the
        // rule is that nothing is interpolated into SQL, and an exception for
        // "obviously safe" values is how the rule stops holding.
        await db.Database
            .ExecuteSqlAsync($"VACUUM INTO {destination}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>pg_dump</c> run inside the database container.
    /// </summary>
    /// <remarks>
    /// Executed in the container rather than from the API, because pg_dump's
    /// version must match the server's and the API image has no Postgres client in
    /// it. Argument vector, never a command line — see CONVENTIONS.md §9.
    /// </remarks>
    private async Task DumpPostgresAsync(string destination, CancellationToken ct)
    {
        var container = await runtime.Containers
            .FindAsync(context.StoreContainerName, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The control-plane database container '{context.StoreContainerName}' was not found, so no "
                + "system backup can be taken.");

        await using var file = File.Create(destination);

        var result = await runtime.Containers.ExecAsync(
            new ExecRequest(
                container.Id,
                ["pg_dump", "--format=custom", "--no-owner", "--dbname", context.StoreDatabase, "--username", context.StoreUser]),
            file,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pg_dump failed with exit code {result.ExitCode}: {result.StandardError}");
        }
    }

    private void CopyKeyRing(string staging)
    {
        if (!Directory.Exists(context.KeyRingPath))
        {
            // Recorded in the manifest rather than thrown. A backup with no key
            // ring is still worth having as a schema and data snapshot — it just
            // must never be restored silently, which VerifyAsync enforces.
            logger.LogWarning(
                "The key ring at {Path} does not exist, so this backup cannot decrypt any stored secret",
                context.KeyRingPath);

            return;
        }

        var target = Path.Combine(staging, KeyRingPrefix.TrimEnd('/'));
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(context.KeyRingPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(context.KeyRingPath, file);
            var destination = Path.Combine(target, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private void WriteManifest(string staging, DateTimeOffset timestamp)
    {
        var manifest = new BackupManifest(
            Version: 1,
            StoreProvider: context.StoreProvider,
            CreatedAt: timestamp,
            KeyRingIncluded: Directory.Exists(Path.Combine(staging, KeyRingPrefix.TrimEnd('/'))),
            AirsideVersion: typeof(SystemBackupProvider).Assembly.GetName().Version?.ToString() ?? "unknown");

        File.WriteAllText(
            Path.Combine(staging, ManifestEntry),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <inheritdoc />
    public async Task<SystemBackupVerification> VerifyAsync(string archivePath, CancellationToken ct)
    {
        if (!File.Exists(archivePath))
        {
            return new SystemBackupVerification(false, null, false, "The archive does not exist.");
        }

        try
        {
            await using var stream = File.OpenRead(archivePath);
            await using var reader = new TarReader(stream);

            BackupManifest? manifest = null;
            var sawStore = false;

            while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
            {
                if (entry.Name is ManifestEntry or "./" + ManifestEntry)
                {
                    await using var memory = new MemoryStream();
                    entry.DataStream?.CopyTo(memory);
                    memory.Position = 0;

                    manifest = await JsonSerializer
                        .DeserializeAsync<BackupManifest>(memory, JsonOptions, ct)
                        .ConfigureAwait(false);
                }
                else if (entry.Name.EndsWith(StoreEntry, StringComparison.Ordinal))
                {
                    sawStore = true;
                }
            }

            if (manifest is null || !sawStore)
            {
                return new SystemBackupVerification(
                    false, null, false,
                    "The archive is missing its manifest or its database dump, so it is not an Airside "
                    + "system backup.");
            }

            // A restore that silently produces an instance which cannot decrypt
            // its own secrets is worse than a refused restore, so this is reported
            // rather than treated as a detail.
            return new SystemBackupVerification(
                IsUsable: true,
                manifest.StoreProvider,
                manifest.KeyRingIncluded,
                manifest.KeyRingIncluded
                    ? null
                    : "This backup contains no Data Protection key ring. Restoring it produces an instance "
                      + "that cannot decrypt any stored password, certificate key, or registry credential.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return new SystemBackupVerification(false, null, false, $"The archive could not be read: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);

        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false))
            .ToLower(CultureInfo.InvariantCulture);
    }

    private sealed record BackupManifest(
        int Version,
        string StoreProvider,
        DateTimeOffset CreatedAt,
        bool KeyRingIncluded,
        string AirsideVersion);
}
