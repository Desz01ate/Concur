namespace Concur;

/// <summary>
/// Configuration options for Go routines.
/// </summary>
public sealed class GoOptions
{
    private SemaphoreSlim? internalSemaphore;
    private readonly object lockObject = new();

    /// <summary>
    /// Optional operation context used for cancellation propagation.
    /// </summary>
    public Context? Context { get; init; }

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
                this.internalSemaphore ??= new SemaphoreSlim(this.MaxConcurrency.Value, this.MaxConcurrency.Value);
            }
        }

        return this.internalSemaphore;
    }
}