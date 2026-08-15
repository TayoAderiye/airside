namespace Airside.Core.Common;

/// <summary>
/// The outcome of an operation that can fail in an expected way.
/// </summary>
/// <remarks>
/// <para>
/// <c>Result</c> is the currency of the service layer. Infrastructure interfaces
/// — <c>IContainerRuntime</c>, <c>IProxyManager</c> — do not use it: a Docker
/// daemon that has vanished is genuinely exceptional and throws, while absence
/// is expressed as a nullable return. Services translate both into results.
/// </para>
/// </remarks>
public readonly struct Result : IEquatable<Result>
{
    private Result(Error? error) => Failure = error;

    public Error? Failure { get; }

    public bool IsSuccess => Failure is null;

    public bool IsFailure => Failure is not null;

    public static Result Ok() => new(error: null);

    public static Result Fail(Error error) => new(error);

    public static Result<T> Ok<T>(T value) => Result<T>.FromValue(value);

    public static Result<T> Fail<T>(Error error) => Result<T>.FromError(error);

    public static implicit operator Result(Error error) => Fail(error);

    public bool Equals(Result other) => Equals(Failure, other.Failure);

    public override bool Equals(object? obj) => obj is Result other && Equals(other);

    public override int GetHashCode() => Failure?.GetHashCode() ?? 0;

    public static bool operator ==(Result left, Result right) => left.Equals(right);

    public static bool operator !=(Result left, Result right) => !left.Equals(right);
}

/// <summary>The outcome of an operation that yields a value or an expected failure.</summary>
public readonly struct Result<T> : IEquatable<Result<T>>
{
    private readonly T? _value;

    private Result(T? value, Error? error)
    {
        _value = value;
        Failure = error;
    }

    public Error? Failure { get; }

    public bool IsSuccess => Failure is null;

    public bool IsFailure => Failure is not null;

    /// <summary>The value. Throws when the result is a failure — check <see cref="IsSuccess"/> first.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Result is a failure ({Failure!.Code}); its value cannot be read.");

    internal static Result<T> FromValue(T value) => new(value, error: null);

    internal static Result<T> FromError(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => FromValue(value);

    public static implicit operator Result<T>(Error error) => FromError(error);

    /// <summary>Discards the value, keeping only success or failure.</summary>
    public Result Discard() => IsSuccess ? Result.Ok() : Result.Fail(Failure!);

    public bool Equals(Result<T> other) =>
        Equals(Failure, other.Failure) && EqualityComparer<T?>.Default.Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is Result<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Failure, _value);

    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);
}
