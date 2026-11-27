namespace Valencia.Core;

/// <summary>A successful value. Separate cases keep Result&lt;string, string&gt; unambiguous.</summary>
public sealed record Ok<T>(T Value);

/// <summary>An expected, typed failure; inspecting it does not throw.</summary>
public sealed record Err<E>(E Error);

/// <summary>
/// A closed result for recoverable operations. Match Case as Ok, Err, or null:
/// default(Result&lt;T, E&gt;) contains null and is neither success nor failure.
/// This is not an exception sandbox, a wire DTO, or a media-buffer transport.
/// </summary>
public readonly struct Result<T, E>
{
    private Result(object value) => Case = value;
    public object? Case { get; }
    public static Result<T, E> Success(T value) => new(new Ok<T>(value));

    public static Result<T, E> Failure(E error) => new(new Err<E>(error));
}
