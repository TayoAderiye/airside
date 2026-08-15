using Airside.Core.Containers;
using DM = Docker.DotNet.Models;

namespace Airside.Runtime.Docker;

/// <summary>
/// Builds exec parameters.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the operations class so the two invariants below can be tested
/// without a Docker daemon. Both are the kind of mistake that produces a
/// plausible-looking backup file that fails only when someone tries to restore it.
/// </para>
/// <para>
/// <b>Tty is always false.</b> With a TTY attached, Docker does not multiplex the
/// output stream — stderr is merged into stdout, so any warning the engine prints
/// lands inside the dump. Docker.DotNet's <c>MultiplexedStream</c> demultiplexes
/// correctly, but only because this flag is false; setting it true silently
/// disables the framing that keeps the two apart.
/// </para>
/// <para>
/// <b>Credentials go in the environment, never in argv.</b> Arguments are visible
/// in the container's process list to anything else running in it, so a password
/// passed as <c>--password=…</c> is readable by the workload itself.
/// </para>
/// </remarks>
internal static class DockerExec
{
    public static DM.ContainerExecCreateParameters ToCreateParameters(ExecRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Argv.Count == 0)
        {
            throw new ArgumentException("An exec request must carry at least one argument.", nameof(request));
        }

        return new DM.ContainerExecCreateParameters
        {
            // An argument vector. Nothing here is a command line, and nothing
            // routes through sh -c.
            Cmd = [.. request.Argv],
            Env = request.Environment is null
                ? null
                : [.. request.Environment.Select(e => $"{e.Key}={e.Value.Reveal()}")],
            WorkingDir = request.WorkingDirectory,
            AttachStdout = true,
            AttachStderr = true,
            AttachStdin = false,
            Tty = false,
        };
    }
}
