namespace InventoryTracker.Services;

/// <summary>A business-rule failure. <paramref name="Field"/> ties it to a form field when applicable.</summary>
public sealed record Error(string Code, string Message, string? Field = null);

/// <summary>Outcome of a service operation that can fail for expected business reasons.</summary>
public class Result
{
    private static readonly IReadOnlyList<Error> NoErrors = [];

    protected Result(IReadOnlyList<Error> errors) => Errors = errors;

    public IReadOnlyList<Error> Errors { get; }

    public bool Succeeded => Errors.Count == 0;

    public static Result Success() => new(NoErrors);

    public static Result Failure(params Error[] errors)
        => errors.Length == 0 ? throw new ArgumentException("A failure needs at least one error.", nameof(errors)) : new(errors);

    public static Result Failure(string code, string message, string? field = null)
        => Failure(new Error(code, message, field));

    public static Result<T> Success<T>(T value) => Result<T>.Ok(value);

    public static Result<T> Failure<T>(params Error[] errors) => Result<T>.Fail(errors);

    public static Result<T> Failure<T>(string code, string message, string? field = null)
        => Result<T>.Fail(new Error(code, message, field));
}

/// <summary>A <see cref="Result"/> that carries a value when successful.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, IReadOnlyList<Error> errors)
        : base(errors) => _value = value;

    /// <summary>The value. Throws if the result is a failure.</summary>
    public T Value => Succeeded
        ? _value!
        : throw new InvalidOperationException("Cannot read Value of a failed result: " + string.Join("; ", Errors.Select(e => e.Message)));

    internal static Result<T> Ok(T value) => new(value, []);

    internal static Result<T> Fail(params Error[] errors)
        => errors.Length == 0 ? throw new ArgumentException("A failure needs at least one error.", nameof(errors)) : new(default, errors);
}
