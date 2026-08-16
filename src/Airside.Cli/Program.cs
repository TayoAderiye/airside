using System.Globalization;
using System.Text.Json;
using Airside.Core.Naming;
using Airside.Core.Operations;

// The escape hatch. Commands are boring and predictable by design — `airside
// deploy`, not `airside takeoff`. The theme is in the name; the CLI is not the
// place for it.
//
// Nothing here talks to the API or the database, and that is the whole point: the
// commands people reach for are the ones they need when the control plane is not
// answering. Everything works from state.json and files on the host.
//
// The state file is read with JsonDocument rather than deserialised into a type.
// This binary is published NativeAOT, and reflection-based serialisation is
// exactly what that cannot do — so the four fields the CLI needs are pulled out by
// name instead of linking the API's model in.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

return args switch
{
    ["status", ..] => Status(),
    ["update", ..] => Update(args),
    ["rollback", ..] => Rollback(),
    ["domain", "reset", ..] => DomainReset(),
    ["backup", "--system", ..] => Backup(),
    ["restore", "--system", var file, ..] => Restore(file),
    ["restore", ..] => Fail("restore --system needs the path to a backup archive."),
    _ => Unknown(args[0]),
};

static void PrintHelp()
{
    Console.WriteLine("airside — control plane for a single Linux server");
    Console.WriteLine();
    Console.WriteLine("Usage: airside <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  status                     Report control-plane and update state");
    Console.WriteLine("  update --version X.Y.Z     Update the control plane");
    Console.WriteLine("  rollback                   Restore the previously running version");
    Console.WriteLine("  backup --system            Back up the control-plane store and key ring");
    Console.WriteLine("  restore --system <file>    Restore a system backup");
    Console.WriteLine("  domain reset               Clear the dashboard domain and restore access by IP");
    Console.WriteLine();
    Console.WriteLine($"State file: {AirsideLabels.HostPaths.State}");
}

/// <summary>
/// Reports what the host looks like without asking the API.
/// </summary>
/// <remarks>
/// The first thing anyone runs when the dashboard will not load, so it reads a
/// file and calls nothing.
/// </remarks>
static int Status()
{
    Console.WriteLine("Airside status");
    Console.WriteLine();

    var state = ReadState();

    if (state is null)
    {
        Console.WriteLine("  No update is in progress.");
    }
    else
    {
        Console.WriteLine($"  Update:        {state.FromVersion} → {state.ToVersion}");
        Console.WriteLine($"  Step:          {state.Step}");
        Console.WriteLine($"  Last written:  {state.UpdatedAt}");

        if (state.BackupPath is not null)
        {
            Console.WriteLine($"  Backup:        {state.BackupPath}");
        }

        if (state.ErrorMessage is not null)
        {
            Console.WriteLine($"  Error:         {state.ErrorMessage}");
        }

        Console.WriteLine();
        Console.WriteLine("  " + UpdateAdvice.For(state.Step).Replace("\n", "\n  ", StringComparison.Ordinal));
    }

    Console.WriteLine();
    Console.WriteLine("  Paths:");
    Console.WriteLine($"    state    {Describe(AirsideLabels.HostPaths.State)}");
    Console.WriteLine($"    key ring {Describe(AirsideLabels.HostPaths.KeyRing)}");
    Console.WriteLine($"    backups  {Describe(AirsideLabels.HostPaths.Backups)}");
    Console.WriteLine($"    data     {Describe(AirsideLabels.HostPaths.Data)}");

    return 0;
}

/// <summary>
/// Prints the commands that perform an update, rather than performing it.
/// </summary>
/// <remarks>
/// Deliberate. Doing the swap properly needs the compose file, its environment,
/// and the Docker socket, and a CLI that reimplemented all three would drift out
/// of step with the compose file the installer wrote — silently, and only
/// noticeably during an update. The API drives updates; this exists for when the
/// API cannot, and there the honest thing is to hand over the exact commands
/// rather than a wrapper that might be subtly wrong.
/// </remarks>
static int Update(string[] args)
{
    var version = ValueOf(args, "--version");

    if (version is null)
    {
        Console.Error.WriteLine("airside: update needs --version X.Y.Z.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("A moving tag cannot be rolled back to, so an explicit version is required.");
        return 64; // EX_USAGE
    }

    if (version.Contains("latest", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("airside: refusing ':latest'.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("There would be no way to record what was running or to roll back to it.");
        return 64;
    }

    Console.WriteLine($"To update to {version}:");
    Console.WriteLine();
    Console.WriteLine("  1. Take a backup first:");
    Console.WriteLine("       airside backup --system");
    Console.WriteLine();
    Console.WriteLine("  2. Pin the version and recreate the control plane:");
    Console.WriteLine($"       AIRSIDE_VERSION={version} \\");
    Console.WriteLine("         docker compose -f /opt/airside/docker-compose.yml \\");
    Console.WriteLine("         up -d airside-api airside-ui");
    Console.WriteLine();
    Console.WriteLine("     Both, named explicitly. The dashboard is a separate container and it");
    Console.WriteLine("     refuses to render against an API of a different version, so updating one");
    Console.WriteLine("     alone leaves you looking at a mismatch screen rather than at Airside.");
    Console.WriteLine();
    Console.WriteLine("  3. Check it came up:");
    Console.WriteLine("       airside status");
    Console.WriteLine();
    Console.WriteLine("Prefer the dashboard when it is reachable — it records the update and takes the");
    Console.WriteLine("backup for you.");

    return 0;
}

static int Rollback()
{
    var state = ReadState();

    if (state?.FromImageDigest is null)
    {
        Console.Error.WriteLine("airside: no previous version is recorded, so there is nothing to roll back to.");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Checked {AirsideLabels.HostPaths.State}.");
        return 69; // EX_UNAVAILABLE
    }

    Console.WriteLine($"Rolling back {state.ToVersion} → {state.FromVersion}");
    Console.WriteLine();

    if (state.AppliedMigrations)
    {
        // Said before the commands rather than after. Airside's migrations are
        // expand-then-contract, so the previous version can read the migrated
        // schema across a single version step — but the operator should know that
        // is what they are relying on.
        Console.WriteLine("  Note: database migrations were applied during this update.");
        Console.WriteLine("  The previous version can read the migrated schema across a single version");
        Console.WriteLine("  step. If this rollback spans more than one, restore the backup instead:");
        Console.WriteLine($"    airside restore --system {state.BackupPath ?? "<backup>"}");
        Console.WriteLine();
    }

    Console.WriteLine("  Recreate the control plane from the exact images that were running:");
    Console.WriteLine($"    AIRSIDE_API_REF={state.FromImageDigest} \\");

    if (state.FromUiImageDigest is { } uiDigest)
    {
        Console.WriteLine($"    AIRSIDE_UI_REF={uiDigest} \\");
        Console.WriteLine("      docker compose -f /opt/airside/docker-compose.yml \\");
        Console.WriteLine("      up -d airside-api airside-ui");
    }
    else
    {
        // No dashboard digest recorded: either this instance predates the split,
        // or the update was prepared by a version that did not know to record it.
        // Rolling the API back alone is still correct, and saying why is better
        // than printing a command with an empty variable in it.
        Console.WriteLine("      docker compose -f /opt/airside/docker-compose.yml up -d airside-api");
        Console.WriteLine();
        Console.WriteLine("  No dashboard image was recorded for the previous version, so only the API");
        Console.WriteLine("  is rolled back here. If the dashboard then reports a version mismatch, pin");
        Console.WriteLine("  it to the same version with AIRSIDE_UI_VERSION and recreate airside-ui.");
    }

    Console.WriteLine();
    Console.WriteLine("  These are image ids rather than tags, so this restores the builds that were");
    Console.WriteLine("  actually running rather than whatever the tags point at now — and it resolves");
    Console.WriteLine("  them locally, which matters when the registry is part of why you are here.");

    return 0;
}

static int Backup()
{
    Console.WriteLine("A system backup covers the control-plane database and the Data Protection key ring.");
    Console.WriteLine();
    Console.WriteLine("  Through the API, which is the supported path:");
    Console.WriteLine("    curl -sS -X POST http://localhost:8080/api/v1/system/backups");
    Console.WriteLine();
    Console.WriteLine("  If the API is not answering, copy both by hand — and keep them together:");
    Console.WriteLine($"    {AirsideLabels.HostPaths.Data}      (the store)");
    Console.WriteLine($"    {AirsideLabels.HostPaths.KeyRing}      (the key ring)");
    Console.WriteLine();
    Console.WriteLine("  Without the key ring, every stored password and certificate key in the backup");
    Console.WriteLine("  is undecryptable. They are worth nothing apart.");

    return 0;
}

static int Restore(string archive)
{
    if (!File.Exists(archive))
    {
        Console.Error.WriteLine($"airside: {archive} does not exist.");
        return 66; // EX_NOINPUT
    }

    Console.WriteLine($"To restore {archive}:");
    Console.WriteLine();
    Console.WriteLine("  1. Stop the control plane, so nothing is writing while it is replaced:");
    Console.WriteLine("       docker stop airside-api");
    Console.WriteLine();
    Console.WriteLine("  2. Unpack the archive:");
    Console.WriteLine("       mkdir -p /tmp/airside-restore");
    Console.WriteLine($"       tar -xf {archive} -C /tmp/airside-restore");
    Console.WriteLine();
    Console.WriteLine("  3. Put back BOTH halves — the store and the key ring:");
    Console.WriteLine($"       cp /tmp/airside-restore/store.dump {AirsideLabels.HostPaths.Data}/airside.db");
    Console.WriteLine($"       cp -r /tmp/airside-restore/keyring/. {AirsideLabels.HostPaths.KeyRing}/");
    Console.WriteLine();
    Console.WriteLine("  4. Start it again:");
    Console.WriteLine("       docker start airside-api");
    Console.WriteLine();
    Console.WriteLine("  Restoring the store without the key ring produces an instance that starts");
    Console.WriteLine("  cleanly and cannot decrypt a single stored secret. Do not skip step 3.");

    return 0;
}

static int DomainReset()
{
    // The way back when the dashboard's own hostname stops resolving. Changing it
    // to a name that does not point here would otherwise be unrecoverable: the API
    // is not published to the host, so there is no address left to reach it on.
    //
    // Writing a file rather than talking to the database keeps this command
    // working when nothing else is. The API consumes the marker on its next start.
    try
    {
        var directory = Path.GetDirectoryName(AirsideLabels.HostPaths.DomainReset)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(AirsideLabels.HostPaths.DomainReset, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
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

/// <summary>
/// The fields of <c>state.json</c> the CLI acts on.
/// </summary>
/// <remarks>
/// Read by name rather than deserialised into the API's model, so this binary
/// stays NativeAOT-clean and keeps working against a state file written by a
/// newer version that added fields.
/// </remarks>
static UpdateSnapshot? ReadState()
{
    if (!File.Exists(AirsideLabels.HostPaths.State))
    {
        return null;
    }

    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AirsideLabels.HostPaths.State));
        var root = document.RootElement;

        return new UpdateSnapshot(
            Text(root, "fromVersion") ?? "unknown",
            Text(root, "toVersion") ?? "unknown",
            Text(root, "fromImageDigest"),
            Text(root, "fromUiImageDigest"),
            Text(root, "step") ?? "unknown",
            Text(root, "updatedAt") ?? "unknown",
            Text(root, "backupPath"),
            Text(root, "errorMessage"),
            root.TryGetProperty("appliedMigrations", out var applied) && applied.ValueKind == JsonValueKind.True);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
    {
        // A truncated file is exactly what a process killed mid-write leaves, and
        // that is the moment this command is being run. Reported as "no state"
        // rather than a stack trace at whoever is trying to recover.
        return null;
    }
}

static string? Text(JsonElement root, string name) =>
    root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static string Describe(string path) =>
    Directory.Exists(path) || File.Exists(path) ? $"{path}  present" : $"{path}  missing";

static string? ValueOf(string[] args, string name)
{
    var index = Array.IndexOf(args, name);

    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"airside: {message}");
    return 64; // EX_USAGE
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"airside: '{command}' is not a command.");
    Console.Error.WriteLine("Run 'airside --help' for the list.");
    return 64; // EX_USAGE
}

internal sealed record UpdateSnapshot(
    string FromVersion,
    string ToVersion,
    string? FromImageDigest,

    /// <summary>Null on an instance whose update predates the dashboard container.</summary>
    string? FromUiImageDigest,
    string Step,
    string UpdatedAt,
    string? BackupPath,
    string? ErrorMessage,
    bool AppliedMigrations);
