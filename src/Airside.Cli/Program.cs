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
    Console.WriteLine();
    Console.WriteLine($"State file: {AirsideLabels.HostPaths.State}");
    return 0;
}

Console.Error.WriteLine($"airside: '{args[0]}' is not implemented yet.");
return 64; // EX_USAGE
