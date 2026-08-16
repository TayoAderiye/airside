using System.Reflection;
using Airside.Api.Features.Operations;
using Airside.Core.Operations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airside.Tests.Operations;

/// <summary>
/// That a control-plane backup which cannot be written says why.
/// </summary>
/// <remarks>
/// <para>
/// This writes a database dump, copies the key ring, and tars the result. Every
/// one of those throws <see cref="IOException"/> when the disk is full — which
/// on a small host is the most likely thing to happen, and is exactly when an
/// operator reaches for this button.
/// </para>
/// <para>
/// The endpoint caught only <see cref="InvalidOperationException"/>, so the
/// common case surfaced as an unhandled 500 carrying no message at all. These
/// assert the provider's failures are translated, because a backup screen that
/// fails silently is worse than one that is missing: it is the screen people use
/// to reassure themselves.
/// </para>
/// </remarks>
public class SystemBackupFailureTests
{
    [Fact]
    public async Task ADiskFullFailureIsReportedRatherThanThrown()
    {
        var result = await Invoke(new IOException("No space left on device"));

        Assert.Equal("backup.system_failed", result.Code);
        Assert.Contains("no space left", result.Detail, StringComparison.OrdinalIgnoreCase);

        // The original text is kept. "No space left on device" is the sentence an
        // operator will search for, and a rewritten message that drops it makes
        // the error harder to place, not easier.
        Assert.Contains("No space left on device", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermissionFailureNamesTheDirectory()
    {
        // Distinguished from the disk-full case on purpose: the remedy is a
        // chown, not free space, and telling someone to clear disk when the
        // directory is root-owned sends them the wrong way.
        var result = await Invoke(new UnauthorizedAccessException("Access to the path is denied"));

        Assert.Equal("backup.system_failed", result.Code);
        Assert.Contains("permission denied", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/var/lib/airside/backups", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALogicalFailureStillReportsTheProvidersOwnMessage()
    {
        // The pre-existing behaviour, asserted so widening the catch did not
        // swallow the case that already worked.
        var result = await Invoke(new InvalidOperationException("No control-plane container found."));

        Assert.Equal("backup.system_failed", result.Code);
        Assert.Equal("No control-plane container found.", result.Detail);
    }

    [Fact]
    public async Task AnUnexpectedFailureIsStillAllowedToEscape()
    {
        // Deliberately not caught. A defect inside Airside turned into a tidy
        // 400 that blames the operator's disk is how a bug goes unreported for a
        // release. Only the two environmental failures are translated.
        await Assert.ThrowsAsync<TimeZoneNotFoundException>(
            () => Invoke(new TimeZoneNotFoundException()));
    }

    /// <summary>
    /// Calls the endpoint handler through the same reflection the route uses,
    /// so the test exercises the real catch clauses rather than a copy of them.
    /// </summary>
    private static async Task<(string Code, string Detail)> Invoke(Exception failure)
    {
        var handler = typeof(OperationsEndpoints)
            .GetMethod("CreateSystemBackupAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task)handler.Invoke(
            null,
            [
                new ThrowingBackupProvider(failure),
                new UpdateOptions { BackupRoot = "/var/lib/airside/backups" },
                NullLoggerFactory.Instance,
                CancellationToken.None,
            ])!;

        await task.ConfigureAwait(false);

        var value = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var problem = (ProblemHttpResult)value.GetType().GetProperty("Result")!.GetValue(value)!;

        return (
            (string)problem.ProblemDetails.Extensions["code"]!,
            problem.ProblemDetails.Detail!);
    }

    private sealed class ThrowingBackupProvider(Exception failure) : ISystemBackupProvider
    {
        public Task<SystemBackupResult> CreateAsync(string destinationDirectory, CancellationToken ct) =>
            throw failure;

        public Task<SystemBackupVerification> VerifyAsync(string archivePath, CancellationToken ct) =>
            throw failure;
    }
}
