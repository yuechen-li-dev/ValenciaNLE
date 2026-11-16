using Valencia.Core;

namespace Valencia.App.Editing;

internal readonly record struct Completed;

/// <summary>UI diagnostics stay local; engine commands retain their existing typed EditResult.</summary>
internal sealed record EditorFailure(string Operation, string Message, bool Cancelled, Exception? Cause);

/// <summary>
/// Translate legacy/third-party exceptions at the application boundary, once.
/// A Result does not undo partially executed work: document changes still belong
/// to EditingSession transactions. Fatal process/resource failures are not recoverable.
/// </summary>
internal static class EditorOperation
{
    public static Result<T, EditorFailure> Try<T>(string operation, Func<T> action)
    {
        try
        {
            return Result<T, EditorFailure>.Success(action());
        }
        catch (Exception error) when (Recoverable(error))
        {
            return Result<T, EditorFailure>.Failure(Failure(operation, error));
        }
    }

    public static async Task<Result<Completed, EditorFailure>> TryAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
            return Result<Completed, EditorFailure>.Success(new());
        }
        catch (Exception error) when (Recoverable(error))
        {
            return Result<Completed, EditorFailure>.Failure(Failure(operation, error));
        }
    }

    private static bool Recoverable(Exception error) =>
        error is not OutOfMemoryException and not AccessViolationException and not StackOverflowException;

    private static EditorFailure Failure(string operation, Exception error) =>
        new(operation, error.Message, error is OperationCanceledException, error);
}
