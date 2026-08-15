using System.Globalization;
using System.Security.Cryptography;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;

namespace Airside.Runtime.Databases;

/// <summary>
/// Shared backup mechanics: run a dump tool inside the container, stream its
/// stdout to the destination, and hash what actually landed.
/// </summary>
internal static class LogicalBackup
{
    /// <summary>
    /// Runs <paramref name="argv"/> in the container and writes its stdout to
    /// <paramref name="destination"/>, returning the artefact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exit code is the truth. A dump tool that fails partway still produces
    /// output, and a non-zero exit with a plausible-looking file is exactly how a
    /// backup that cannot be restored gets recorded as successful.
    /// </para>
    /// <para>
    /// The hash is computed over the bytes as they are written, not by re-reading
    /// the file afterwards, so it describes what was actually captured rather than
    /// what is on disk some time later.
    /// </para>
    /// </remarks>
    public static async Task<BackupArtifact> RunAsync(
        IContainerRuntime runtime,
        string containerId,
        IReadOnlyList<string> argv,
        IReadOnlyList<EnvironmentEntry> environment,
        Stream destination,
        string engineSnapshot,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);

        using var hasher = new HashingStream(destination);

        var result = await runtime.Containers
            .ExecAsync(new ExecRequest(containerId, argv, environment), hasher, ct)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            progress?.Report(result.StandardError);

            throw new InvalidOperationException(
                $"The dump command exited with code {result.ExitCode}. "
                + $"The partial output must not be treated as a backup. {Trim(result.StandardError)}");
        }

        if (hasher.BytesWritten == 0)
        {
            // An empty dump with a zero exit code means the tool ran and produced
            // nothing, which restores as an empty database — the failure this
            // whole feature exists to avoid.
            throw new InvalidOperationException(
                "The dump command produced no output. Refusing to record an empty backup.");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            // Warnings on a successful dump are worth surfacing but are not a
            // failure — and, critically, they never end up inside the payload:
            // stdout and stderr arrive on separate framed channels.
            progress?.Report(result.StandardError);
        }

        return new BackupArtifact(hasher.BytesWritten, hasher.Hash, engineSnapshot, BackupKind.Logical);
    }

    public static async Task RestoreAsync(
        IContainerRuntime runtime,
        string containerId,
        IReadOnlyList<string> argv,
        IReadOnlyList<EnvironmentEntry> environment,
        Stream source,
        CancellationToken ct)
    {
        // Docker's exec API cannot stream stdin through Airside's runtime
        // interface, so the dump is staged inside the container's own /tmp and
        // fed to the restore tool from there. Both the directory and the file
        // name are fixed constants — nothing here comes from a request.
        await runtime.Containers
            .CopyIntoContainerAsync(containerId, StagingDirectory, StagingFile, source, ct)
            .ConfigureAwait(false);

        var result = await runtime.Containers
            .ExecAsync(new ExecRequest(containerId, argv, environment), null, ct)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The restore command exited with code {result.ExitCode}. {Trim(result.StandardError)}");
        }
    }

    public const string StagingDirectory = "/tmp";

    public const string StagingFile = "airside-restore.dump";

    private static string Trim(string text) =>
        text.Length <= 500 ? text : text[..500] + "…";
}

/// <summary>
/// Passes bytes through to an inner stream while hashing them and counting.
/// </summary>
/// <remarks>
/// Hashing in flight rather than re-reading afterwards means the recorded digest
/// covers exactly the bytes that were captured — including if the stream was
/// truncated, which is the case the digest exists to detect.
/// </remarks>
internal sealed class HashingStream(Stream inner) : Stream, IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _finalHash;

    public long BytesWritten { get; private set; }

    public string Hash => _finalHash ??= Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => BytesWritten;

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        _hash.AppendData(buffer);
        BytesWritten += buffer.Length;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _hash.AppendData(buffer.Span);
        BytesWritten += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ = Hash;
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Verifies a backup before anything irreversible happens.</summary>
public static class BackupVerification
{
    /// <summary>
    /// Checks that the stored engine version can restore into the target.
    /// </summary>
    /// <remarks>
    /// Refused before the database is stopped, not during the restore. A pg_dump
    /// from 16 does not load into 15, and finding out halfway through means the
    /// database is already down with a half-applied dump inside it.
    /// </remarks>
    public static Result CheckEngineCompatibility(string backupSnapshot, string targetSnapshot)
    {
        var backupMajor = MajorVersionOf(backupSnapshot);
        var targetMajor = MajorVersionOf(targetSnapshot);

        if (backupMajor is null || targetMajor is null)
        {
            return new Error(
                ErrorCodes.BackupEngineVersionMismatch,
                "The engine version of this backup cannot be determined, so it cannot be verified as "
                + "restorable. Restore it manually if you are certain.",
                Metadata(backupSnapshot, targetSnapshot));
        }

        // Restoring forward across a major version is a migration, not a restore,
        // and it needs pg_upgrade or a dump-and-load the operator drives.
        // Restoring backward cannot work at all.
        if (backupMajor != targetMajor)
        {
            return new Error(
                ErrorCodes.BackupEngineVersionMismatch,
                $"This backup came from {backupSnapshot} and the database is now {targetSnapshot}. "
                + "Restoring across a major version is a migration, not a restore.",
                Metadata(backupSnapshot, targetSnapshot));
        }

        return Result.Ok();
    }

    public static Result CheckChecksum(string? expected, string actual)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return new Error(
                ErrorCodes.BackupChecksumMismatch,
                "This backup has no recorded checksum, so its integrity cannot be verified.");
        }

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : new Error(
                ErrorCodes.BackupChecksumMismatch,
                "The backup file does not match its recorded checksum. It is corrupt or truncated, "
                + "and restoring it would replace a working database with a broken one.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["expected"] = expected,
                    ["actual"] = actual,
                });
    }

    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static string? MajorVersionOf(string snapshot)
    {
        var separator = snapshot.LastIndexOf(':');

        if (separator < 0 || separator == snapshot.Length - 1)
        {
            return null;
        }

        var version = snapshot[(separator + 1)..];
        var dot = version.IndexOf('.', StringComparison.Ordinal);
        var major = dot < 0 ? version : version[..dot];

        return int.TryParse(major, CultureInfo.InvariantCulture, out _) ? major : null;
    }

    private static Dictionary<string, object?> Metadata(string backup, string target) =>
        new(StringComparer.Ordinal)
        {
            ["backupEngine"] = backup,
            ["targetEngine"] = target,
        };
}
