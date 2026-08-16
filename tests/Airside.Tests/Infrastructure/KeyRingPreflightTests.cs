using Airside.Api.Infrastructure;

namespace Airside.Tests.Infrastructure;

/// <summary>
/// That an unusable key ring stops the control plane instead of the first login.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a real install. The installer created
/// <c>/var/lib/airside/keys</c> as root with mode 0700 and the API image runs as
/// a non-root user, so the one process that needed the directory could not read
/// it. Nothing noticed: Data Protection does not touch the key ring until
/// something is first encrypted, and the first thing encrypted is the session
/// cookie at the first login.
/// </para>
/// <para>
/// So the control plane started, reported healthy, applied every migration,
/// seeded its permissions, accepted the setup token, created the administrator —
/// and then answered the first login with <c>internal.unhandled</c>. Every signal
/// available to an operator said the install had worked.
/// </para>
/// </remarks>
public sealed class KeyRingPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "airside-keyring-" + Guid.CreateVersion7().ToString("N")[..12]);

    public KeyRingPreflightTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone; nothing to clean up.
        }
    }

    [Fact]
    public void AWritableKeyRingPasses()
    {
        var path = Path.Combine(_root, "keys");

        KeyRingPreflight.Verify(path);

        Assert.True(Directory.Exists(path), "the directory should be created if absent");
    }

    [Fact]
    public void TheProbeLeavesNothingBehind()
    {
        // The check writes a file to prove it can. Leaving it there would put an
        // unexplained file in the one directory an operator is told to back up.
        var path = Path.Combine(_root, "keys");

        KeyRingPreflight.Verify(path);

        Assert.Empty(Directory.GetFileSystemEntries(path));
    }

    [Fact]
    public void AnUnusableKeyRingThrowsWithSomethingActionable()
    {
        // A path *underneath a file* rather than a chmod'd directory: running the
        // test suite as root would defeat a permission bit, and CI has been root
        // before now. This fails identically for every uid.
        var file = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(file, "");

        var error = Assert.Throws<InvalidOperationException>(
            () => KeyRingPreflight.Verify(Path.Combine(file, "keys")));

        // The message is the whole point of failing here rather than later, so
        // it is asserted rather than assumed: the path, and the command to fix it.
        Assert.Contains("not writable", error.Message, StringComparison.Ordinal);
        Assert.Contains("chown", error.Message, StringComparison.Ordinal);
        Assert.Contains(file, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOriginalFailureIsKeptAsTheInnerException()
    {
        var file = Path.Combine(_root, "not-a-directory-either");
        File.WriteAllText(file, "");

        var error = Assert.Throws<InvalidOperationException>(
            () => KeyRingPreflight.Verify(Path.Combine(file, "keys")));

        Assert.NotNull(error.InnerException);
    }
}
