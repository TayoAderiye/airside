using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airside.Core.Operations;

/// <summary>
/// Where an update has got to, on disk.
/// </summary>
/// <remarks>
/// <para>
/// Written to a host path before every step, because the process running the
/// update is the process being replaced. If the updater dies between stopping the
/// old container and starting the new one, nothing is left running that knows
/// what was happening — no API to ask, no job row being advanced, and a host with
/// no control plane on it.
/// </para>
/// <para>
/// This file is what the CLI reads to finish the job by hand. It is deliberately
/// small, plain JSON, and independent of the database: the database may be exactly
/// what is unreachable.
/// </para>
/// </remarks>
public sealed record UpdateState
{
    /// <summary>Matches the <c>UpdateRecord</c> row, so the two can be reconciled afterwards.</summary>
    public required Guid UpdateId { get; init; }

    public required string FromVersion { get; init; }

    public required string ToVersion { get; init; }

    /// <summary>
    /// The digest the previous version ran.
    /// </summary>
    /// <remarks>
    /// A digest rather than a tag. Rolling back to <c>:0.1.0</c> gets whatever that
    /// tag points at today, which after a re-push is not what was running — and a
    /// rollback that restores a different build than the one that worked is not a
    /// rollback.
    /// </remarks>
    public required string? FromImageDigest { get; init; }

    public required string? ToImageDigest { get; init; }

    /// <summary>The digests for the dashboard container, which is updated alongside the API.</summary>
    /// <remarks>
    /// Not <c>required</c>, unlike the pair above, and that is deliberate rather
    /// than an oversight. An update already in flight when this version arrives
    /// left a state file written without these fields, and that file is read by
    /// the code that has to recover it. Making them required would turn the one
    /// artefact recovery depends on into one that fails to parse.
    /// </remarks>
    public string? FromUiImageDigest { get; init; }

    public string? ToUiImageDigest { get; init; }

    public required UpdateStep Step { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? BackupPath { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether migrations have run.
    /// </summary>
    /// <remarks>
    /// The one fact that decides whether rollback is safe. Airside's migrations are
    /// expand-then-contract, so the previous version can read a newly migrated
    /// schema — but only within one version step, and the CLI has to know rather
    /// than assume.
    /// </remarks>
    public bool AppliedMigrations { get; init; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static UpdateState? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateState>(json, Options);
        }
        catch (JsonException)
        {
            // A truncated file is exactly what a process killed mid-write leaves.
            // Reported as "no usable state" rather than throwing at whoever is
            // trying to recover.
            return null;
        }
    }
}
