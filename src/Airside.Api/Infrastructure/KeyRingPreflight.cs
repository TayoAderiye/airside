namespace Airside.Api.Infrastructure;

/// <summary>
/// Refuses to start when the Data Protection key ring is not writable.
/// </summary>
/// <remarks>
/// <para>
/// The key ring encrypts every stored secret and signs every session cookie, but
/// nothing touches it until the first thing is encrypted — and on a fresh install
/// that is the session cookie issued at the first login. So an unwritable key
/// ring does not fail at startup. It fails after the control plane has come up
/// healthy, applied its migrations, seeded its permissions, accepted the setup
/// token and created the administrator account, at which point the operator is
/// shown "internal.unhandled" for what is a file permission.
/// </para>
/// <para>
/// Found on the first real Linux install. The installer created
/// <c>/var/lib/airside/keys</c> as root with mode 0700; the API image runs as a
/// non-root user; the directory was therefore unreadable by the only process
/// that needed it. Failing here turns a mystifying 500 into a message naming the
/// path, the user, and the command that fixes it.
/// </para>
/// </remarks>
internal static class KeyRingPreflight
{
    public static void Verify(string keyRingPath)
    {
        var probe = Path.Combine(keyRingPath, $".airside-write-probe-{Environment.ProcessId}");

        try
        {
            Directory.CreateDirectory(keyRingPath);

            // Written and deleted rather than merely stat'd. A directory can look
            // present and still refuse a write — which is the failure being
            // guarded against, and the one a permissions check by inspection
            // misses.
            File.WriteAllText(probe, "airside");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var user = Environment.UserName;

            throw new InvalidOperationException(
                $"""
                 The Data Protection key ring at '{keyRingPath}' is not writable.

                 Airside is running as '{user}' (uid {GetUid()}) and cannot write there, so it
                 could not issue a session cookie or decrypt a stored secret. Refusing to start
                 rather than accept a login it cannot complete.

                 On the host:

                   sudo chown -R {GetUid()}:{GetUid()} /var/lib/airside
                   sudo chmod 700 /var/lib/airside/keys
                   sudo docker restart airside-api

                 Underlying error: {ex.Message}
                 """,
                ex);
        }
    }

    /// <summary>
    /// The effective uid, or <c>?</c> off Linux.
    /// </summary>
    /// <remarks>
    /// Reported because it is the number the operator has to put in the chown,
    /// and it is not something they can easily find out about a container whose
    /// image has no shell to run <c>id</c> in.
    /// </remarks>
    private static string GetUid()
    {
        // /proc rather than a p/invoke: the BCL exposes no getuid, and taking a
        // dependency to decorate an error message would be a poor trade.
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    continue;
                }

                // "Uid:\treal\teffective\tsaved\tfilesystem" — the effective one
                // is what the kernel checks the write against.
                var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);

                return fields.Length > 2 ? fields[2] : fields[^1];
            }
        }
#pragma warning disable CA1031 // The uid is decoration on an error message; never worth throwing over.
        catch
#pragma warning restore CA1031
        {
            // Not Linux, or /proc is not mounted. The rest of the message still
            // says everything that matters.
        }

        return "the API's user";
    }
}
