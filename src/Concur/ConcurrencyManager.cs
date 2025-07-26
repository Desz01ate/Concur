namespace Concur;

/// <summary>
/// Manages concurrency limits for Go routines through semaphores.
/// Provides global and group-based concurrency control.
/// </summary>
internal static class ConcurrencyManager
{
    private const int DefaultMaxCacheCapacity = 50;

    private readonly static LruCache<SemaphoreSlim> MaxConcurrencyCache = new(maxCapacity: DefaultMaxCacheCapacity);

    /// <summary>
    /// Gets the current number of cached MaxConcurrency semaphores.
    /// </summary>
    public static int CurrentCacheSize => MaxConcurrencyCache.Count;

    /// <summary>
    /// Gets the maximum capacity of the MaxConcurrency semaphore cache.
    /// Default is 50. This is set at startup and cannot be changed at runtime.
    /// </summary>
    public static int MaxCacheCapacity => DefaultMaxCacheCapacity;

    /// <summary>
    /// Gets the appropriate semaphore based on the provided options.
    /// Returns null if no concurrency limiting should be applied.
    /// </summary>
    /// <param name="options">The Go options containing concurrency configuration.</param>
    /// <returns>A semaphore to use for concurrency limiting, or null if no limiting should be applied.</returns>
    public static SemaphoreSlim? GetSemaphore(GoOptions? options)
    {
        if (options == null)
        {
            return null;
        }

        // Priority 1: Custom semaphore
        if (options.ConcurrencyLimiter != null)
        {
            return options.ConcurrencyLimiter;
        }

        // Priority 2: MaxConcurrency (create or reuse semaphore with LRU cache)
        if (options.MaxConcurrency.HasValue)
        {
            var maxConcurrency = options.MaxConcurrency.Value;

            if (maxConcurrency <= 0)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency, nameof(options.MaxConcurrency));
            }

            return MaxConcurrencyCache.GetOrAdd(maxConcurrency, count => new SemaphoreSlim(count, count));
        }

        return null;
    }

    /// <summary>
    /// Clears all managed semaphores and disposes of them.
    /// This method is primarily intended for testing scenarios.
    /// </summary>
    public static void ClearAll()
    {
        MaxConcurrencyCache.Clear();
    }
}