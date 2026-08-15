using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Airside.Core.Common;

/// <summary>
/// A validated identifier safe to embed in a container name, volume name, network
/// name, or proxy matcher.
/// </summary>
/// <remarks>
/// <para>
/// This is the most load-bearing validation in the system. Every managed Docker
/// object name is derived from a slug, so a type that cannot hold an invalid
/// value means no downstream code has to wonder whether it was checked.
/// </para>
/// <para>
/// Input is rejected, never sanitised (CONVENTIONS.md §4). Uppercase input is an
/// error rather than something quietly lowercased — a caller that meant
/// <c>Orders</c> should be told, not silently given <c>orders</c>, which may
/// already belong to something else.
/// </para>
/// </remarks>
public readonly partial struct Slug : IEquatable<Slug>
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    private readonly string? _value;

    private Slug(string value) => _value = value;

    public string Value => _value ?? throw new InvalidOperationException("Slug is uninitialised.");

    public static bool TryCreate(string? candidate, out Slug slug)
    {
        if (!string.IsNullOrEmpty(candidate)
            && candidate.Length is >= MinLength and <= MaxLength
            && Pattern().IsMatch(candidate)
            && !candidate.Contains("--", StringComparison.Ordinal))
        {
            slug = new Slug(candidate);
            return true;
        }

        slug = default;
        return false;
    }

    public static Result<Slug> Create(string? candidate) =>
        TryCreate(candidate, out var slug)
            ? slug
            : new Error(
                ErrorCodes.ValidationInvalidSlug,
                "Must be 3–32 characters, lowercase, starting with a letter, ending alphanumeric, "
                + "using only letters, digits, and single hyphens.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = candidate,
                    ["pattern"] = "^[a-z][a-z0-9-]{1,30}[a-z0-9]$",
                });

    public override string ToString() => _value ?? string.Empty;

    public bool Equals(Slug other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Slug other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value ?? string.Empty);

    public static bool operator ==(Slug left, Slug right) => left.Equals(right);

    public static bool operator !=(Slug left, Slug right) => !left.Equals(right);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,30}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
