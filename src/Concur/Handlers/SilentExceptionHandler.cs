namespace Concur.Handlers;

using Abstractions;

/// <summary>
/// A no-op exception handler that silently ignores exceptions.
/// </summary>
public sealed class SilentExceptionHandler : IExceptionHandler
{
    /// <summary>
    /// Gets the singleton instance of the silent exception handler.
    /// </summary>
    public static SilentExceptionHandler Instance { get; } = new();

    /// <summary>
    /// Private constructor to enforce singleton pattern.
    /// </summary>
    private SilentExceptionHandler()
    {
    }

    /// <summary>
    /// Handles an exception by doing nothing (silent handling).
    /// </summary>
    /// <param name="context">The exception context.</param>
    /// <returns>A completed task.</returns>
    public ValueTask HandleAsync(IExceptionContext context) => ValueTask.CompletedTask;
}