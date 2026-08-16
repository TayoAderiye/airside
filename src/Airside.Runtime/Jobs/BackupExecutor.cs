using Airside.Core.Databases;

namespace Airside.Runtime.Jobs;

/// <summary>
/// Performs a backup: dump, hash, verify, move into place.
/// </summary>
/// <remarks>
/// Extracted so the restore handler runs exactly the same routine for its
/// pre-restore safety backup. It previously only wrote the row, which meant the
/// safety net existed as a record and not as a file — the worst possible shape
/// for a safety net, because it reads as present right up until someone needs it.
/// </remarks>
public sealed class BackupExecutor(IDatabaseEngineRegistry engines)
{
    public async Task<BackupArtifact> RunAsync(
        DatabaseWorkloadSnapshot workload,
        string storagePath,
        string engineSnapshot,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workload);

        if (workload.ContainerId is null)
        {
            throw new InvalidOperationException("The database has no container to back up.");
        }

        var engine = engines.Get(workload.Engine);
        var temporaryPath = storagePath + ".partial";

        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

        BackupArtifact artifact;

        try
        {
            // Written to a partial path first. A file that appears at its final
            // path while still being written is one a scheduled restore can pick
            // up half-finished.
            await using (var file = File.Create(temporaryPath))
            {
                artifact = await engine.BackupAsync(
                    new BackupOperation(
                        new DatabaseEndpoint(
                            workload.ContainerId,
                            workload.ContainerName,
                            engine.Capabilities.DefaultPort,
                            workload.Spec.DatabaseName),
                        new DatabaseCredentialValue(workload.Spec.Username, workload.Spec.Password),
                        workload.DataVolumeName,
                        engineSnapshot,
                        progress),
                    file,
                    ct).ConfigureAwait(false);
            }

            // Re-hashed from disk rather than trusted from the in-flight hash. If
            // the two disagree the bytes did not survive the write, which is
            // exactly what a checksum is for.
            await using (var written = File.OpenRead(temporaryPath))
            {
                var onDisk = await Databases.BackupVerification
                    .ComputeSha256Async(written, ct)
                    .ConfigureAwait(false);

                if (!string.Equals(onDisk, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The backup file on disk does not match what was streamed out of the database.");
                }
            }

            File.Move(temporaryPath, storagePath, overwrite: true);
            return artifact;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }
}
