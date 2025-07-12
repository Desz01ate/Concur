namespace Concur.Contexts;

using Abstractions;

/// <summary>
/// Provides context information about an exception that occurred in a Go routine.
/// </summary>
internal sealed class ExceptionContext : IExceptionContext
{
    /// <summary>
    /// The exception that was thrown.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// A unique identifier for the Go routine where the exception occurred.
    /// </summary>
    public string RoutineId { get; }

    /// <summary>
    /// Optional operation name or description for debugging purposes.
    /// </summary>
    public string? OperationName { get; }

    /// <summary>
    /// The time when the exception occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Additional metadata about the operation.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExceptionContext"/> class.
    /// </summary>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="routineId">A unique identifier for the Go routine.</param>
    /// <param name="operationName">Optional operation name for debugging purposes.</param>
    /// <param name="metadata">Additional metadata about the operation.</param>
    public ExceptionContext(
        Exception exception, 
        string routineId, 
        string? operationName = null, 
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        this.Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        this.RoutineId = routineId ?? throw new ArgumentNullException(nameof(routineId));
        this.OperationName = operationName;
        this.Timestamp = DateTimeOffset.UtcNow;
        this.Metadata = metadata ?? new Dictionary<string, object?>();
    }
}