namespace Concur.Tests.Channels;

using System.Collections.Concurrent;
using Abstractions;

public sealed class QueueChannel<T> : IChannel<T, QueueChannel<T>>
{
    private readonly ConcurrentQueue<T> queue;
    private readonly SemaphoreSlim semaphore;
    private readonly int? capacity;
    private readonly object lockObject = new();

    private volatile bool isCompleted;
    private volatile Exception? completionException;
    private volatile int currentCount;

    public QueueChannel(int? capacity = null)
    {
        this.queue = new ConcurrentQueue<T>();
        this.semaphore = new SemaphoreSlim(0, int.MaxValue);
        this.capacity = capacity;
        this.currentCount = 0;
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        if (this.isCompleted)
        {
            throw new InvalidOperationException("Cannot write to a completed channel.");
        }

        // Handle capacity constraints for bounded channels
        if (this.capacity.HasValue)
        {
            while (true)
            {
                lock (this.lockObject)
                {
                    if (this.isCompleted)
                    {
                        throw new InvalidOperationException("Cannot write to a completed channel.");
                    }

                    if (this.currentCount < this.capacity.Value)
                    {
                        this.queue.Enqueue(item);
                        Interlocked.Increment(ref this.currentCount);
                        this.semaphore.Release();
                        return;
                    }
                }

                // Wait a bit before retrying if at capacity
                await Task.Delay(1, cancellationToken);
            }
        }

        // Unbounded channel - just enqueue
        if (this.isCompleted)
        {
            throw new InvalidOperationException("Cannot write to a completed channel.");
        }

        this.queue.Enqueue(item);
        Interlocked.Increment(ref this.currentCount);
        this.semaphore.Release();
    }

    /// <inheritdoc/>
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        lock (this.lockObject)
        {
            if (!this.isCompleted)
            {
                this.isCompleted = true;
                this.semaphore.Release(); // Signal any waiting readers
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask FailAsync(Exception ex, CancellationToken cancellationToken = default)
    {
        lock (this.lockObject)
        {
            if (!this.isCompleted)
            {
                this.isCompleted = true;
                this.completionException = ex;
                this.semaphore.Release(); // Signal any waiting readers
            }
        }

        return ValueTask.CompletedTask;
    }

    public static QueueChannel<T> operator <<(QueueChannel<T> channel, T item)
    {
        channel.WriteAsync(item).GetAwaiter().GetResult();
        return channel;
    }

    public static T operator -(QueueChannel<T> channel)
    {
        return channel.queue.TryDequeue(out var item) ? item : default!;
    }

    /// <inheritdoc/>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new QueueChannelAsyncEnumerator(this, cancellationToken);
    }

    private sealed class QueueChannelAsyncEnumerator : IAsyncEnumerator<T>
    {
        private readonly QueueChannel<T> channel;
        private readonly CancellationToken cancellationToken;
        private T current = default!;

        public QueueChannelAsyncEnumerator(QueueChannel<T> channel, CancellationToken cancellationToken)
        {
            this.channel = channel;
            this.cancellationToken = cancellationToken;
        }

        public T Current => this.current;

        public async ValueTask<bool> MoveNextAsync()
        {
            while (true)
            {
                // First, try to dequeue an item
                if (this.channel.queue.TryDequeue(out var item))
                {
                    this.current = item;
                    Interlocked.Decrement(ref this.channel.currentCount);
                    return true;
                }

                // If no item available, check if channel is completed
                if (this.channel.isCompleted)
                {
                    // Check for completion exception
                    if (this.channel.completionException != null)
                    {
                        throw this.channel.completionException;
                    }

                    // Channel is completed and no more items
                    return false;
                }

                // Wait for new items or completion
                try
                {
                    await this.channel.semaphore.WaitAsync(this.cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}