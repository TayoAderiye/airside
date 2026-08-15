namespace Airside.Core.Common;

/// <summary>
/// A value that must never be logged, serialised, or interpolated by accident.
/// </summary>
/// <remarks>
/// <para>
/// The point of this type is that the careless thing is safe. Interpolating a
/// <see cref="Secret"/> into a log message, a URL, or an exception yields
/// <c>***</c>, so the usual way secrets escape — someone reaching for string
/// concatenation while debugging — produces nothing useful to an attacker.
/// Reading the real value requires saying <see cref="Reveal"/> out loud.
/// </para>
/// <para>
/// A reference type rather than a struct, deliberately: value equality would
/// invite comparing secrets with <c>==</c>, which is both a timing-attack surface
/// and almost never what the caller actually wants.
/// </para>
/// <para>
/// This is defence in depth, not containment. A <see cref="Secret"/> is a plain
/// managed string in memory; it is not pinned, zeroed, or protected from a
/// process dump. It protects against accidental disclosure, not against an
/// attacker who already has the process.
/// </para>
/// </remarks>
public sealed class Secret
{
    public const string Mask = "***";

    private readonly string _value;

    public Secret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public bool IsEmpty => _value.Length == 0;

    public int Length => _value.Length;

    /// <summary>
    /// Returns the underlying value. Every call site is a deliberate decision and
    /// should be reviewable — never call this to build a log or error message.
    /// </summary>
    public string Reveal() => _value;

    /// <summary>Always <c>***</c>. This is the entire purpose of the type.</summary>
    public override string ToString() => Mask;
}
