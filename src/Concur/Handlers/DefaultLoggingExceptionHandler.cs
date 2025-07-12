namespace Concur.Handlers;

using Abstractions;

/// <summary>
/// Exception handler that logs to console in debug mode.
/// </summary>
public sealed class DefaultLoggingExceptionHandler : IExceptionHandler
{
    /// <summary>
    /// Handles an exception by logging it to the console in debug mode.
    /// </summary>
    /// <param name="context">The exception context.</param>
    /// <returns>A completed task.</returns>
    public ValueTask HandleAsync(IExceptionContext context)
    {
#if DEBUG
        Console.WriteLine($"[ConcurRoutine] Exception in routine '{context.RoutineId}': {context.Exception}");
        if (!string.IsNullOrEmpty(context.OperationName))
        {
            Console.WriteLine($"  Operation: {context.OperationName}");
        }
#endif
        return ValueTask.CompletedTask;
    }
}