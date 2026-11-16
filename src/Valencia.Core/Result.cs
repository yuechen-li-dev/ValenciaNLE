namespace Valencia.Core;

/// <summary>A successful value. Separate cases keep Result&lt;string, string&gt; unambiguous.</summary>
public sealed record Ok<T>(T Value);

/// <summary>An expected, typed failure; inspecting it does not throw.</summary>
public sealed record Err<E>(E Error);

/// <summary>
/// A C# 15 union for recoverable operations. Match Ok, Err, and null explicitly:
/// default(Result&lt;T, E&gt;) contains null and is neither success nor failure.
/// This is not an exception sandbox, a wire DTO, or a media-buffer transport.
/// </summary>
public union Result<T, E>(Ok<T>, Err<E>)
{
    public static Result<T, E> Success(T value) => new Ok<T>(value);

    public static Result<T, E> Failure(E error) => new Err<E>(error);
}
