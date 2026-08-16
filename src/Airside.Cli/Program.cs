using Airside.Core.Naming;

// The escape hatch. Commands are boring and predictable by design — `airside
// deploy`, not `airside takeoff`. The theme is in the name; the CLI is not the
// place for it.
//
// Commands land in Phase 1 alongside the job system and the runtime abstraction.
// This entry point exists now to hold the shape and to prove the linked-source
// sharing of AirsideLabels compiles without a project reference.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("airside — control plane for a single Linux server");
    Console.WriteLine();
    Console.WriteLine("Usage: airside <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  update [--version X.Y.Z]   Update the control plane");
    Console.WriteLine("  rollback                   Restore the previously running version");
    Console.WriteLine("  backup --system            Back up the control-plane store and key ring");
    Console.WriteLine("  restore --system <file>    Restore a system backup");
    Console.WriteLine("  status                     Report control-plane and workload state");
    Console.WriteLine("  domain reset               Clear the dashboard domain and restore access by IP");
    Console.WriteLine();
    Console.WriteLine($"State file: {AirsideLabels.HostPaths.State}");
    return 0;
}

// The way back when the dashboard's own hostname stops resolving. Changing it to
// a name that does not point here would otherwise be unrecoverable: the API is
// not published to the host, so there is no address left to reach it on.
//
// Writing a file rather than talking to the database keeps this command working
// when nothing else is — the CLI has no dependencies and needs none. The API
// consumes the marker on its next start.
if (args is ["domain", "reset", ..])
{
    try
    {
        var directory = Path.GetDirectoryName(AirsideLabels.HostPaths.DomainReset)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(AirsideLabels.HostPaths.DomainReset, DateTimeOffset.UtcNow.ToString("O"));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"airside: could not write {AirsideLabels.HostPaths.DomainReset}: {ex.Message}");
        Console.Error.WriteLine("Run this as a user that can write to /var/lib/airside, usually with sudo.");
        return 73; // EX_CANTCREAT
    }

    Console.WriteLine("The dashboard domain will be cleared when the control plane next starts.");
    Console.WriteLine();
    Console.WriteLine("Restart it now:");
    Console.WriteLine("  docker restart airside-api");
    Console.WriteLine();
    Console.WriteLine("Airside will then be reachable on this server's address again.");
    return 0;
}

Console.Error.WriteLine($"airside: '{args[0]}' is not implemented yet.");
return 64; // EX_USAGE
