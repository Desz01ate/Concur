namespace Concur.Abstractions;

/// <summary>
/// Provides context information about an exception that occurred in a Go routine.
/// </summary>
public interface IExceptionContext
{
    /// <summary>
    /// The exception that was thrown.
    /// </summary>
    Exception Exception { get; }

    /// <summary>
    /// A unique identifier for the Go routine where the exception occurred.
    /// </summary>
    string RoutineId { get; }

    /// <summary>
    /// Optional operation name or description for debugging purposes.
    /// </summary>
    string? OperationName { get; }

    /// <summary>
    /// The time when the exception occurred.
    /// </summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Additional metadata about the operation.
    /// </summary>
    IReadOnlyDictionary<string, object?> Metadata { get; }
}