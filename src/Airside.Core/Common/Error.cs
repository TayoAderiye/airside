namespace Airside.Core.Common;

/// <summary>
/// An expected failure. Errors are values, not exceptions — see CONVENTIONS.md §3.
/// </summary>
/// <param name="Code">
/// Stable, dotted, lowercase. This is an API contract: the UI switches on it and
/// renaming one is a breaking change.
/// </param>
/// <param name="Message">
/// For humans. Must never contain a secret, a connection string, or a raw
/// message from an external system.
/// </param>
/// <param name="Metadata">
/// The machine-readable specifics. Clients read these; they never parse
/// <paramref name="Message"/>.
/// </param>
public sealed record Error(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public Error WithMetadata(params ReadOnlySpan<KeyValuePair<string, object?>> entries)
    {
        var merged = Metadata is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(Metadata, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            merged[entry.Key] = entry.Value;
        }

        return this with { Metadata = merged };
    }
}
