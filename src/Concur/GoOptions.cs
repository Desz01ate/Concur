namespace Concur;

using Abstractions;

/// <summary>
/// Configuration options for Go routines.
/// </summary>
public sealed class GoOptions
{
    private SemaphoreSlim? InternalSemaphore;
    private readonly object lockObject = new();

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
    /// If provided, MaxConcurrency is ignored.
    /// </summary>
    public SemaphoreSlim? ConcurrencyLimiter { get; init; }

    internal SemaphoreSlim? GetOrCreateSemaphore()
    {
        // Highest priority.
        if (this.ConcurrencyLimiter is not null)
        {
            return this.ConcurrencyLimiter;
        }

        if (this.MaxConcurrency is not null)
        {
            lock (this.lockObject)
            {
                this.InternalSemaphore ??= new SemaphoreSlim(this.MaxConcurrency.Value, this.MaxConcurrency.Value);
            }
        }

        return this.InternalSemaphore;
    }
}