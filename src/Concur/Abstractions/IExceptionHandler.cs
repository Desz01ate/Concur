namespace Concur.Abstractions;

/// <summary>
/// Handles exceptions that occur in Go routines.
/// </summary>
public interface IExceptionHandler
{
    /// <summary>
    /// Handles an exception that occurred in a Go routine.
    /// </summary>
    /// <param name="context">The exception context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    ValueTask HandleAsync(IExceptionContext context);
}