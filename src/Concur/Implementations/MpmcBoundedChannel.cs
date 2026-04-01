namespace Concur.Implementations;

using Abstractions;

/// <summary>
/// A bounded, striped, multi-producer multi-consumer channel.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class MpmcBoundedChannel<T> : IChannel<T>
{
    private const int CompletionStateOpen = 0;
    private const int CompletionStateCompleted = 1;
    private const int CompletionStateFailing = 2;
    private const int CompletionStateFailed = 3;
    private const int WriteSpinCount = 64;
    private const int ReadSpinCount = 64;

    private readonly Shard[] shards;
    private readonly AsyncCounterGate availableSlots;
    private readonly AsyncCounterGate availableItems;
    private readonly CancellationTokenSource completionSignal;
    private readonly TaskCompletionSource<bool> drainedSignal;

    private readonly int shardCount;

    private int writeCursor;
    private long nextReaderShardSeed;
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
        this.availableSlots = new AsyncCounterGate(capacity, capacity);
        this.availableItems = new AsyncCounterGate(0, capacity);
        this.completionSignal = new CancellationTokenSource();
        this.drainedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var spinsSinceThreadYield = 0;
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
                spinsSinceThreadYield++;

                if (spinsSinceThreadYield < WriteSpinCount)
                {
                    continue;
                }

                spinsSinceThreadYield = 0;
                if (!Thread.Yield())
                {
                    Thread.Sleep(0);
                }
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
        if (Interlocked.CompareExchange(
                ref this.completionState,
                CompletionStateCompleted,
                CompletionStateOpen) != CompletionStateOpen)
        {
            return ValueTask.CompletedTask;
        }

        this.completionSignal.Cancel();
        this.SignalDrainedIfCompleted();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask FailAsync(Exception ex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (Interlocked.CompareExchange(
                ref this.completionState,
                CompletionStateFailing,
                CompletionStateOpen) != CompletionStateOpen)
        {
            return ValueTask.CompletedTask;
        }

        Volatile.Write(ref this.completionException, ex);
        Volatile.Write(ref this.completionState, CompletionStateFailed);
        this.completionSignal.Cancel();
        this.SignalDrainedIfCompleted();
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
        var basePerShard = (capacity + shardCount - 1) / shardCount;
        var shardCapacity = NextPowerOfTwo(Math.Max(2, basePerShard));

        for (var i = 0; i < shardCount; i++)
        {
            shards[i] = new Shard(shardCapacity);
        }

        return shards;
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
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

    private bool TryDequeueReserved(ref int startShard, out T item)
    {
        for (var i = 0; i < this.shardCount; i++)
        {
            var idx = (startShard + i) % this.shardCount;
            if (!this.shards[idx].TryDequeue(out item))
            {
                continue;
            }

            var remainingItems = Interlocked.Decrement(ref this.pendingItems);
            this.availableSlots.Release();

            if (remainingItems == 0)
            {
                this.SignalDrainedIfCompleted();
            }

            startShard = (idx + 1) % this.shardCount;
            return true;
        }

        startShard = (startShard + 1) % this.shardCount;
        item = default!;
        return false;
    }

    private bool IsCompletedAndDrained()
    {
        var completionState = Volatile.Read(ref this.completionState);
        return IsTerminalCompletionState(completionState) &&
            Interlocked.Read(ref this.pendingItems) == 0;
    }

    private void ThrowIfCompleted()
    {
        if (Volatile.Read(ref this.completionState) != CompletionStateOpen)
        {
            throw new InvalidOperationException("Cannot write to a completed channel.");
        }
    }

    private void SignalDrainedIfCompleted()
    {
        if (!IsTerminalCompletionState(Volatile.Read(ref this.completionState)))
        {
            return;
        }

        if (Interlocked.Read(ref this.pendingItems) != 0)
        {
            return;
        }

        this.drainedSignal.TrySetResult(true);
    }

    private static bool IsTerminalCompletionState(int completionState)
    {
        return completionState == CompletionStateCompleted || completionState == CompletionStateFailed;
    }

    private async ValueTask WaitForSlotAsync(CancellationToken cancellationToken)
    {
        var spin = new SpinWait();
        for (var i = 0; i < WriteSpinCount; i++)
        {
            this.ThrowIfCompleted();

            if (this.availableSlots.TryAcquire())
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            spin.SpinOnce();
        }

        if (!cancellationToken.CanBeCanceled)
        {
            try
            {
                await this.availableSlots.AcquireAsync(this.completionSignal.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                this.ThrowIfCompleted();
                throw;
            }
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            this.completionSignal.Token);

        try
        {
            await this.availableSlots.AcquireAsync(linkedCts.Token).ConfigureAwait(false);
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

    private async ValueTask<ConsumerWaitResult> WaitForItemPermitAsync(CancellationToken cancellationToken)
    {
        var spin = new SpinWait();
        for (var i = 0; i < ReadSpinCount; i++)
        {
            if (this.availableItems.TryAcquire())
            {
                return ConsumerWaitResult.ItemPermitAcquired;
            }

            if (this.IsCompletedAndDrained())
            {
                return ConsumerWaitResult.ChannelCompleted;
            }

            cancellationToken.ThrowIfCancellationRequested();
            spin.SpinOnce();
        }

        if (this.IsCompletedAndDrained())
        {
            return ConsumerWaitResult.ChannelCompleted;
        }

        if (Volatile.Read(ref this.completionState) != CompletionStateOpen)
        {
            try
            {
                if (cancellationToken.CanBeCanceled)
                {
                    await this.drainedSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await this.drainedSignal.Task.ConfigureAwait(false);
                }

                return ConsumerWaitResult.ChannelCompleted;
            }
            catch (OperationCanceledException)
            {
                return ConsumerWaitResult.ConsumerCanceled;
            }
        }

        if (!cancellationToken.CanBeCanceled)
        {
            try
            {
                await this.availableItems.AcquireAsync(this.completionSignal.Token).ConfigureAwait(false);
                return ConsumerWaitResult.ItemPermitAcquired;
            }
            catch (OperationCanceledException)
            {
                await this.drainedSignal.Task.ConfigureAwait(false);
                return ConsumerWaitResult.ChannelCompleted;
            }
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            this.completionSignal.Token);

        try
        {
            await this.availableItems.AcquireAsync(linkedCts.Token).ConfigureAwait(false);
            return ConsumerWaitResult.ItemPermitAcquired;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ConsumerWaitResult.ConsumerCanceled;
        }
        catch (OperationCanceledException)
        {
            try
            {
                await this.drainedSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return ConsumerWaitResult.ChannelCompleted;
            }
            catch (OperationCanceledException)
            {
                return ConsumerWaitResult.ConsumerCanceled;
            }
        }
    }

    private sealed class Enumerator : IAsyncEnumerator<T>
    {
        private readonly MpmcBoundedChannel<T> owner;
        private readonly CancellationToken cancellationToken;
        private int nextShard;
        private T current = default!;
        public Enumerator(MpmcBoundedChannel<T> owner, CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.cancellationToken = cancellationToken;
            this.nextShard = (int)(Interlocked.Increment(ref owner.nextReaderShardSeed) % owner.shardCount);
        }

        public T Current => this.current;

        public async ValueTask<bool> MoveNextAsync()
        {
            while (true)
            {
                var waitResult = await this.owner.WaitForItemPermitAsync(this.cancellationToken).ConfigureAwait(false);

                switch (waitResult)
                {
                    case ConsumerWaitResult.ItemPermitAcquired:
                        this.current = this.WaitForReservedItem();
                        return true;

                    case ConsumerWaitResult.ChannelCompleted:
                        var completionException = Volatile.Read(ref this.owner.completionException);
                        if (completionException is not null)
                        {
                            throw completionException;
                        }

                        return false;

                    case ConsumerWaitResult.ConsumerCanceled:
                        return false;

                    case ConsumerWaitResult.Retry:
                        continue;

                    default:
                        throw new InvalidOperationException("Unknown consumer wait result.");
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private T WaitForReservedItem()
        {
            var spin = new SpinWait();
            var spinsSinceThreadYield = 0;

            while (true)
            {
                if (this.owner.TryDequeueReserved(ref this.nextShard, out var item))
                {
                    return item;
                }

                spin.SpinOnce();
                spinsSinceThreadYield++;

                if (spinsSinceThreadYield < ReadSpinCount)
                {
                    continue;
                }

                spinsSinceThreadYield = 0;
                if (!Thread.Yield())
                {
                    Thread.Sleep(0);
                }
            }
        }
    }

    private sealed class Shard
    {
        private readonly Slot[] slots;
        private readonly int mask;
        private readonly int capacity;

        private long enqueuePos;
        private long dequeuePos;

        public Shard(int capacity)
        {
            this.capacity = capacity;
            this.mask = capacity - 1;
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
                var slot = this.slots[(int)pos & this.mask];
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
                var slot = this.slots[(int)pos & this.mask];
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

    private sealed class AsyncCounterGate
    {
        private readonly object sync = new();
        private readonly Queue<Waiter> waiters = new();
        private readonly int maxCount;
        private int count;

        public AsyncCounterGate(int initialCount, int maxCount)
        {
            if (maxCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCount), "Max count must be greater than zero.");
            }

            if (initialCount < 0 || initialCount > maxCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialCount),
                    "Initial count must be in the inclusive range [0, maxCount].");
            }

            this.count = initialCount;
            this.maxCount = maxCount;
        }

        public int CurrentCount => Volatile.Read(ref this.count);

        public bool TryAcquire()
        {
            while (true)
            {
                var current = Volatile.Read(ref this.count);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref this.count, current - 1, current) == current)
                {
                    return true;
                }
            }
        }

        public ValueTask AcquireAsync(CancellationToken cancellationToken = default)
        {
            if (this.TryAcquire())
            {
                return ValueTask.CompletedTask;
            }

            return this.AcquireSlowAsync(cancellationToken);
        }

        public void Release()
        {
            this.Release(1);
        }

        public void Release(int releaseCount)
        {
            if (releaseCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(releaseCount), "Release count must be greater than zero.");
            }

            for (var i = 0; i < releaseCount; i++)
            {
                lock (this.sync)
                {
                    while (this.waiters.Count > 0)
                    {
                        if (this.waiters.Dequeue().TrySignal())
                        {
                            goto ReleaseCompleted;
                        }
                    }

                    if (!this.TryIncrementCount())
                    {
                        throw new SemaphoreFullException();
                    }
                }

            ReleaseCompleted:
                continue;
            }
        }

        private async ValueTask AcquireSlowAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Waiter waiter;
            lock (this.sync)
            {
                if (this.TryAcquire())
                {
                    return;
                }

                waiter = new Waiter();
                this.waiters.Enqueue(waiter);
            }

            await waiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private bool TryIncrementCount()
        {
            while (true)
            {
                var current = Volatile.Read(ref this.count);
                if (current >= this.maxCount)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref this.count, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private sealed class Waiter
        {
            private readonly TaskCompletionSource<bool> completionSource =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool TrySignal()
            {
                return this.completionSource.TrySetResult(true);
            }

            public ValueTask WaitAsync(CancellationToken cancellationToken)
            {
                if (!cancellationToken.CanBeCanceled)
                {
                    return new ValueTask(this.completionSource.Task);
                }

                return this.WaitWithCancellationAsync(cancellationToken);
            }

            private async ValueTask WaitWithCancellationAsync(CancellationToken cancellationToken)
            {
                using var registration = cancellationToken.Register(
                    static state =>
                    {
                        var tcs = (TaskCompletionSource<bool>)state!;
                        tcs.TrySetCanceled();
                    },
                    this.completionSource);

                await this.completionSource.Task.ConfigureAwait(false);
            }
        }
    }

    private enum ConsumerWaitResult
    {
        ItemPermitAcquired,
        ChannelCompleted,
        ConsumerCanceled,
        Retry,
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
