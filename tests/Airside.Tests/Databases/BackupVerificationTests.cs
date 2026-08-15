using System.Text;
using Airside.Core.Common;
using Airside.Runtime.Databases;

namespace Airside.Tests.Databases;

/// <summary>
/// The checks that run before a restore touches anything.
/// </summary>
/// <remarks>
/// A truncated backup that restores as an empty database is the worst failure
/// this product has, because it looks like it worked. Everything here exists to
/// refuse before the database is stopped rather than after.
/// </remarks>
public class BackupVerificationTests
{
    [Fact]
    public void SameMajorVersion_IsCompatible()
    {
        Assert.True(BackupVerification.CheckEngineCompatibility("postgres:16", "postgres:16").IsSuccess);
        Assert.True(BackupVerification.CheckEngineCompatibility("postgres:16.4", "postgres:16.6").IsSuccess);
    }

    [Fact]
    public void DifferentMajorVersion_IsRefused()
    {
        // Restoring forward across a major is a migration needing pg_upgrade;
        // restoring backward cannot work at all. Either way it is not a restore.
        var result = BackupVerification.CheckEngineCompatibility("postgres:16", "postgres:15");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.BackupEngineVersionMismatch, result.Failure!.Code);
        Assert.Equal("postgres:16", result.Failure.Metadata!["backupEngine"]);
        Assert.Equal("postgres:15", result.Failure.Metadata["targetEngine"]);
    }

    [Fact]
    public void UpgradeDirection_IsAlsoRefused()
    {
        Assert.True(BackupVerification.CheckEngineCompatibility("postgres:15", "postgres:17").IsFailure);
    }

    [Fact]
    public void UnparseableVersion_IsRefusedRatherThanAssumedSafe()
    {
        // Failing closed. "We could not tell" must not become "probably fine"
        // when the consequence is a half-applied dump in a stopped database.
        Assert.True(BackupVerification.CheckEngineCompatibility("postgres", "postgres:16").IsFailure);
        Assert.True(BackupVerification.CheckEngineCompatibility("postgres:latest", "postgres:16").IsFailure);
    }

    [Fact]
    public void MatchingChecksum_Passes()
    {
        Assert.True(BackupVerification.CheckChecksum("abc123", "ABC123").IsSuccess);
    }

    [Fact]
    public void MismatchedChecksum_IsRefusedWithBothValues()
    {
        var result = BackupVerification.CheckChecksum("abc123", "def456");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.BackupChecksumMismatch, result.Failure!.Code);
        Assert.Equal("abc123", result.Failure.Metadata!["expected"]);
        Assert.Equal("def456", result.Failure.Metadata["actual"]);
    }

    [Fact]
    public void MissingChecksum_IsRefused()
    {
        // A backup with no recorded digest cannot be verified, and an unverifiable
        // backup is not a backup you restore over a working database.
        Assert.True(BackupVerification.CheckChecksum(null, "abc").IsFailure);
        Assert.True(BackupVerification.CheckChecksum(string.Empty, "abc").IsFailure);
    }

    [Fact]
    public async Task ComputeSha256_MatchesAKnownValue()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("airside"));

        var hash = await BackupVerification.ComputeSha256Async(stream, CancellationToken.None);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    [Fact]
    public async Task TruncatedContent_ProducesADifferentHash()
    {
        // The whole point: a backup that lost its tail must not verify.
        using var full = new MemoryStream(Encoding.UTF8.GetBytes("the complete dump payload"));
        using var truncated = new MemoryStream(Encoding.UTF8.GetBytes("the complete dump"));

        var fullHash = await BackupVerification.ComputeSha256Async(full, CancellationToken.None);
        var truncatedHash = await BackupVerification.ComputeSha256Async(truncated, CancellationToken.None);

        Assert.NotEqual(fullHash, truncatedHash);
        Assert.True(BackupVerification.CheckChecksum(fullHash, truncatedHash).IsFailure);
    }
}

public class HashingStreamTests
{
    [Fact]
    public async Task PassesBytesThroughWhileHashingThem()
    {
        using var destination = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("dump contents");

        string hash;
        long written;

        using (var hashing = new HashingStreamProbe(destination))
        {
            await hashing.WriteAsync(payload, CancellationToken.None);
            hash = hashing.Hash;
            written = hashing.BytesWritten;
        }

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(payload.Length, written);

        using var verify = new MemoryStream(payload);
        Assert.Equal(await BackupVerification.ComputeSha256Async(verify, CancellationToken.None), hash);
    }

    [Fact]
    public void EmptyStream_ReportsZeroBytes()
    {
        // The zero-byte case is what catches a dump tool that exited cleanly
        // having produced nothing — which restores as an empty database.
        using var destination = new MemoryStream();
        using var hashing = new HashingStreamProbe(destination);

        Assert.Equal(0, hashing.BytesWritten);
    }
}

/// <summary>HashingStream is internal to Airside.Runtime; this reaches it for testing.</summary>
internal sealed class HashingStreamProbe : Stream
{
    private readonly Stream _inner;

    public HashingStreamProbe(Stream destination)
    {
        var type = typeof(BackupVerification).Assembly.GetType("Airside.Runtime.Databases.HashingStream")!;
        _inner = (Stream)Activator.CreateInstance(type, destination)!;
    }

    public long BytesWritten => (long)_inner.GetType().GetProperty("BytesWritten")!.GetValue(_inner)!;

    public string Hash => (string)_inner.GetType().GetProperty("Hash")!.GetValue(_inner)!;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _inner.Length;

    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
