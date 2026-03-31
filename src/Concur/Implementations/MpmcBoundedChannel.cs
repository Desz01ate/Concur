namespace Concur.Implementations;

using Abstractions;

/// <summary>
/// A bounded, striped, multi-producer multi-consumer channel.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class MpmcBoundedChannel<T> : IChannel<T>
{
    private const int WriteSpinCount = 32;

    private readonly Shard[] shards;
    private readonly SemaphoreSlim availableSlots;
    private readonly SemaphoreSlim availableItems;
    private readonly CancellationTokenSource completionSignal;

    private readonly int shardCount;

    private int writeCursor;
    private int readCursor;
    private long pendingItems;

    private int completionState;
    private Exception? completionException;

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

        var requestedShardCount = shardCount ?? Math.Min(Environment.ProcessorCount, 8);
        if (requestedShardCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shardCount), "Shard count must be greater than zero.");
        }

        this.shardCount = Math.Min(requestedShardCount, capacity);
        this.shards = CreateShards(capacity, this.shardCount);
        this.availableSlots = new SemaphoreSlim(capacity, capacity);
        this.availableItems = new SemaphoreSlim(0, int.MaxValue);
        this.completionSignal = new CancellationTokenSource();
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        this.ThrowIfCompleted();
        await this.WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.TryEnqueue(item))
            {
                Interlocked.Increment(ref this.pendingItems);
                this.availableItems.Release();
                return;
            }

            this.ThrowIfCompleted();

            var spin = new SpinWait();
            while (true)
            {
                if (this.TryEnqueue(item))
                {
                    Interlocked.Increment(ref this.pendingItems);
                    this.availableItems.Release();
                    return;
                }

                this.ThrowIfCompleted();
                spin.SpinOnce();
            }
        }
        catch
        {
            this.availableSlots.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref this.completionState, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        this.completionSignal.Cancel();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask FailAsync(Exception ex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (Interlocked.Exchange(ref this.completionState, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        this.completionException = ex;
        this.completionSignal.Cancel();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new Enumerator(this, cancellationToken);
    }

    private static Shard[] CreateShards(int capacity, int shardCount)
    {
        var shards = new Shard[shardCount];
        var baseCapacity = capacity / shardCount;
        var remainder = capacity % shardCount;

        for (var i = 0; i < shardCount; i++)
        {
            var shardCapacity = baseCapacity + (i < remainder ? 1 : 0);
            shards[i] = new Shard(shardCapacity);
        }

        return shards;
    }

    private bool TryEnqueue(T item)
    {
        var start = (Interlocked.Increment(ref this.writeCursor) & int.MaxValue) % this.shardCount;
        for (var i = 0; i < this.shardCount; i++)
        {
            var idx = (start + i) % this.shardCount;
            if (this.shards[idx].TryEnqueue(item))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryRead(out T item)
    {
        var start = (Interlocked.Increment(ref this.readCursor) & int.MaxValue) % this.shardCount;
        for (var i = 0; i < this.shardCount; i++)
        {
            var idx = (start + i) % this.shardCount;
            if (!this.shards[idx].TryDequeue(out item))
            {
                continue;
            }

            Interlocked.Decrement(ref this.pendingItems);
            this.availableSlots.Release();
            return true;
        }

        item = default!;
        return false;
    }

    private bool IsCompletedAndDrained()
    {
        return Volatile.Read(ref this.completionState) != 0 &&
            Interlocked.Read(ref this.pendingItems) == 0;
    }

    private void ThrowIfCompleted()
    {
        if (Volatile.Read(ref this.completionState) != 0)
        {
            throw new InvalidOperationException("Cannot write to a completed channel.");
        }
    }

    private async ValueTask WaitForSlotAsync(CancellationToken cancellationToken)
    {
        var spin = new SpinWait();
        for (var i = 0; i < WriteSpinCount; i++)
        {
            this.ThrowIfCompleted();

            if (this.availableSlots.Wait(0))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            spin.SpinOnce();
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            this.completionSignal.Token);

        try
        {
            await this.availableSlots.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            this.ThrowIfCompleted();
            throw;
        }
    }

    private sealed class Enumerator : IAsyncEnumerator<T>
    {
        private readonly MpmcBoundedChannel<T> owner;
        private readonly CancellationToken cancellationToken;
        private T current = default!;
        public Enumerator(MpmcBoundedChannel<T> owner, CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.cancellationToken = cancellationToken;
        }

        public T Current => this.current;

        public async ValueTask<bool> MoveNextAsync()
        {
            while (true)
            {
                if (this.owner.TryRead(out var item))
                {
                    this.current = item;
                    return true;
                }

                if (this.owner.IsCompletedAndDrained())
                {
                    if (this.owner.completionException is not null)
                    {
                        throw this.owner.completionException;
                    }

                    return false;
                }

                try
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        this.cancellationToken,
                        this.owner.completionSignal.Token);

                    await this.owner.availableItems.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (this.cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Shard
    {
        private readonly Slot[] slots;
        private readonly int capacity;

        private long enqueuePos;
        private long dequeuePos;

        public Shard(int capacity)
        {
            this.capacity = capacity;
            this.slots = new Slot[capacity];

            for (var i = 0; i < capacity; i++)
            {
                this.slots[i] = new Slot(i);
            }
        }

        public bool TryEnqueue(T item)
        {
            var spin = new SpinWait();
            while (true)
            {
                var pos = Volatile.Read(ref this.enqueuePos);
                var slot = this.slots[(int)(pos % this.capacity)];
                var sequence = Volatile.Read(ref slot.Sequence);
                var delta = sequence - pos;

                if (delta == 0)
                {
                    if (Interlocked.CompareExchange(ref this.enqueuePos, pos + 1, pos) != pos)
                    {
                        spin.SpinOnce();
                        continue;
                    }

                    slot.Item = item;
                    Volatile.Write(ref slot.Sequence, pos + 1);
                    return true;
                }

                if (delta < 0)
                {
                    return false;
                }

                spin.SpinOnce();
            }
        }

        public bool TryDequeue(out T item)
        {
            var spin = new SpinWait();
            while (true)
            {
                var pos = Volatile.Read(ref this.dequeuePos);
                var slot = this.slots[(int)(pos % this.capacity)];
                var sequence = Volatile.Read(ref slot.Sequence);
                var delta = sequence - (pos + 1);

                if (delta == 0)
                {
                    if (Interlocked.CompareExchange(ref this.dequeuePos, pos + 1, pos) != pos)
                    {
                        spin.SpinOnce();
                        continue;
                    }

                    item = slot.Item;
                    slot.Item = default!;
                    Volatile.Write(ref slot.Sequence, pos + this.capacity);
                    return true;
                }

                if (delta < 0)
                {
                    item = default!;
                    return false;
                }

                spin.SpinOnce();
            }
        }
    }

    private sealed class Slot
    {
        public Slot(long sequence)
        {
            this.Sequence = sequence;
        }

        public T Item = default!;
        public long Sequence;
    }
}
