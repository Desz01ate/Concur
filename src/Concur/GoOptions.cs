namespace Concur;

using Abstractions;

/// <summary>
/// Configuration options for Go routines.
/// </summary>
public sealed class GoOptions
{
    /// <summary>
    /// The exception handler to use for this operation. If null, uses the default handler.
    /// </summary>
    public IExceptionHandler? ExceptionHandler { get; init; }

    /// <summary>
    /// Optional operation name for debugging and logging purposes.
    /// </summary>
    public string? OperationName { get; init; }

    /// <summary>
    /// Additional metadata to include with the exception context.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = 
        new Dictionary<string, object?>();

    /// <summary>
    /// Maximum number of concurrent executions allowed for this operation.
    /// If specified, this operation will wait for an available slot before executing.
    /// Takes precedence over ConcurrencyGroup limits.
    /// </summary>
    public int? MaxConcurrency { get; init; }

    /// <summary>
    /// A custom semaphore to control concurrency for this specific operation.
    /// If provided, MaxConcurrency and ConcurrencyGroup are ignored.
    /// </summary>
    public SemaphoreSlim? ConcurrencyLimiter { get; init; }

    /// <summary>
    /// Gets the default options instance.
    /// </summary>
    public static GoOptions Default { get; } = new();
}