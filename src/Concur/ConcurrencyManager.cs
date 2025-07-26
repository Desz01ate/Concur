using System.Collections.Concurrent;

namespace Concur;

/// <summary>
/// Manages concurrency limits for Go routines through semaphores.
/// Provides global and group-based concurrency control.
/// </summary>
internal static class ConcurrencyManager
{
    private readonly static ConcurrentDictionary<string, SemaphoreSlim> GroupLimiters = new();
    private readonly static ConcurrentDictionary<int, SemaphoreSlim> MaxConcurrencyLimiters = new();

    /// <summary>
    /// Sets a global concurrency limit for a specific group name.
    /// </summary>
    /// <param name="groupName">The name of the concurrency group.</param>
    /// <param name="maxConcurrency">The maximum number of concurrent executions allowed.</param>
    public static void SetGroupLimit(string groupName, int maxConcurrency)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            throw new ArgumentException("Group name cannot be null or whitespace.", nameof(groupName));
        }

        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be greater than zero.");
        }

        GroupLimiters.AddOrUpdate(groupName,
            _ => new SemaphoreSlim(maxConcurrency, maxConcurrency),
            (_, existing) =>
            {
                existing.Dispose();
                return new SemaphoreSlim(maxConcurrency, maxConcurrency);
            });
    }

    /// <summary>
    /// Removes a group limit, disposing of the associated semaphore.
    /// </summary>
    /// <param name="groupName">The name of the concurrency group to remove.</param>
    /// <returns>True if the group was removed, false if it didn't exist.</returns>
    public static bool RemoveGroupLimit(string groupName)
    {
        if (GroupLimiters.TryRemove(groupName, out var semaphore))
        {
            semaphore.Dispose();

            return true;
        }

        return false;
    }

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
            return options.ConcurrencyLimiter;

        // Priority 2: MaxConcurrency (create or reuse semaphore)
        if (options.MaxConcurrency.HasValue)
        {
            var maxConcurrency = options.MaxConcurrency.Value;
            if (maxConcurrency <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrency),
                    "MaxConcurrency must be greater than zero.");

            return MaxConcurrencyLimiters.GetOrAdd(maxConcurrency,
                count => new SemaphoreSlim(count, count));
        }

        // Priority 3: ConcurrencyGroup
        if (!string.IsNullOrWhiteSpace(options.ConcurrencyGroup))
        {
            return GroupLimiters.GetValueOrDefault(options.ConcurrencyGroup);
        }

        return null;
    }

    /// <summary>
    /// Clears all managed semaphores and disposes of them.
    /// This method is primarily intended for testing scenarios.
    /// </summary>
    internal static void ClearAll()
    {
        foreach (var semaphore in GroupLimiters.Values)
        {
            semaphore.Dispose();
        }

        GroupLimiters.Clear();

        foreach (var semaphore in MaxConcurrencyLimiters.Values)
        {
            semaphore.Dispose();
        }

        MaxConcurrencyLimiters.Clear();
    }
}