namespace Concur.Implementations;

using Abstractions;

/// <summary>
/// A bounded, striped, multi-producer multi-consumer channel.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class MpmcBoundedChannel<T> : IChannel<T>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MpmcBoundedChannel{T}"/> class.
    /// </summary>
    /// <param name="capacity">Maximum buffered item count.</param>
    /// <param name="shardCount">Optional number of shards to use for contention reduction.</param>
    public MpmcBoundedChannel(int capacity, int? shardCount = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        var resolvedShardCount = shardCount ?? Math.Min(Environment.ProcessorCount, 8);
        if (resolvedShardCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shardCount), "Shard count must be greater than zero.");
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(T item, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask FailAsync(Exception ex, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
