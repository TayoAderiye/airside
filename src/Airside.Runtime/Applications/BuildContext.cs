using Airside.Core.Common;
using System.Text;

namespace Airside.Runtime.Applications;

/// <summary>
/// Validates the paths that reach a Docker build.
/// </summary>
/// <remarks>
/// The build context is a directory Airside tars up and streams to the daemon. A
/// Dockerfile path that escapes it would let a repository read files from outside
/// its own checkout — the control plane's key ring lives on the same filesystem —
/// so escape is rejected rather than normalised.
/// </remarks>
public static class BuildContextPaths
{
    /// <summary>
    /// Checks that a path stays inside the build context.
    /// </summary>
    /// <remarks>
    /// Resolved against the root before comparing, so <c>a/../../etc/passwd</c> is
    /// caught as readily as <c>../etc/passwd</c>. Absolute paths and paths
    /// containing a null byte are refused outright.
    /// </remarks>
    public static Result<string> ResolveWithin(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Rejected("The path is empty.");
        }

        if (relativePath.Contains('\0', StringComparison.Ordinal))
        {
            return Rejected("The path contains a null byte.");
        }

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('~'))
        {
            return Rejected("The path must be relative to the build context.");
        }

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath));

        // The separator matters: without it, /build-context-evil would pass a
        // naive StartsWith against /build-context.
        var boundary = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        return combined.StartsWith(boundary, StringComparison.Ordinal)
            ? combined
            : Rejected("The path escapes the build context.");
    }

    private static Error Rejected(string reason) => new(
        ErrorCodes.ValidationFailed,
        $"Invalid path: {reason}",
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "dockerfilePath" });
}

/// <summary>
/// Caps a build log at a size worth storing.
/// </summary>
/// <remarks>
/// Head and tail are kept and the middle dropped, because the useful parts of a
/// failed build are the first error and the last line. A naive truncation keeps
/// the head and loses the failure, which is the one thing anybody opens the log
/// for.
/// </remarks>
public static class BuildLog
{
    public const int MaxBytes = 256 * 1024;

    public static (string Content, bool Truncated) Cap(string log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (Encoding.UTF8.GetByteCount(log) <= MaxBytes)
        {
            return (log, false);
        }

        var lines = log.Split('\n');
        var head = new List<string>();
        var tail = new List<string>();
        var budget = MaxBytes / 2;

        var used = 0;

        foreach (var line in lines)
        {
            var size = Encoding.UTF8.GetByteCount(line) + 1;

            if (used + size > budget)
            {
                break;
            }

            head.Add(line);
            used += size;
        }

        used = 0;

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var size = Encoding.UTF8.GetByteCount(lines[i]) + 1;

            if (used + size > budget)
            {
                break;
            }

            tail.Insert(0, lines[i]);
            used += size;
        }

        var dropped = lines.Length - head.Count - tail.Count;

        return (
            string.Join('\n', head)
            + $"\n\n... {dropped} lines omitted ...\n\n"
            + string.Join('\n', tail),
            true);
    }
}
