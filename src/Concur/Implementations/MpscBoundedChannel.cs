namespace Concur.Implementations;

using System.Threading.Channels;
using Abstractions;

/// <summary>
/// A bounded, striped, multi-producer single-consumer channel optimized for throughput
/// and low allocation in high-contention producer scenarios.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class MpscBoundedChannel<T> : IChannel<T>
{
    private const int WriteSpinCount = 32;

    private readonly Channel<T>[] stripes;
    private readonly SemaphoreSlim availableSlots;
    private readonly SemaphoreSlim availableItems;

    private readonly int stripeCount;
    private int writeCursor;
    private int readCursor;
    private long pendingItems;

    private int completionState;
    private Exception? completionException;

    /// <summary>
    /// Initializes a new instance of the <see cref="MpscBoundedChannel{T}"/> class.
    /// </summary>
    /// <param name="capacity">Maximum number of buffered items across all producers.</param>
    /// <param name="stripeCount">
    /// Optional number of producer stripes. If null, a runtime-based default is selected.
    /// </param>
    public MpscBoundedChannel(int capacity, int? stripeCount = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        this.stripeCount = stripeCount ?? Math.Min(Math.Min(Environment.ProcessorCount, capacity), 8);
        if (this.stripeCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stripeCount), "Stripe count must be greater than zero.");
        }

        this.stripes = new Channel<T>[this.stripeCount];
        for (var i = 0; i < this.stripeCount; i++)
        {
            this.stripes[i] = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true,
            });
        }

        this.availableSlots = new SemaphoreSlim(capacity, capacity);
        this.availableItems = new SemaphoreSlim(0, int.MaxValue);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        this.ThrowIfCompleted();
        await this.WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

        if (this.TryEnqueue(item))
        {
            return;
        }

        this.availableSlots.Release();
        this.ThrowIfCompleted();
        throw new InvalidOperationException("Cannot write to a completed channel.");
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref this.completionState, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var stripe in this.stripes)
        {
            stripe.Writer.TryComplete();
        }

        this.availableItems.Release();
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
        foreach (var stripe in this.stripes)
        {
            stripe.Writer.TryComplete(ex);
        }

        this.availableItems.Release();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new Enumerator(this, cancellationToken);
    }

    private bool TryEnqueue(T item)
    {
        var start = (Interlocked.Increment(ref this.writeCursor) & int.MaxValue) % this.stripeCount;
        for (var i = 0; i < this.stripeCount; i++)
        {
            var idx = (start + i) % this.stripeCount;
            if (!this.stripes[idx].Writer.TryWrite(item))
            {
                continue;
            }

            Interlocked.Increment(ref this.pendingItems);
            this.availableItems.Release();
            return true;
        }

        return false;
    }

    private bool TryRead(out T item)
    {
        var start = this.readCursor;
        for (var i = 0; i < this.stripeCount; i++)
        {
            var idx = (start + i) % this.stripeCount;
            if (!this.stripes[idx].Reader.TryRead(out var candidate))
            {
                continue;
            }

            item = candidate;
            this.readCursor = (idx + 1) % this.stripeCount;
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
            if (this.availableSlots.Wait(0))
            {
                return;
            }

            spin.SpinOnce();
        }

        await this.availableSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Enumerator : IAsyncEnumerator<T>
    {
        private readonly MpscBoundedChannel<T> owner;
        private readonly CancellationToken cancellationToken;
        private T current = default!;

        public Enumerator(MpscBoundedChannel<T> owner, CancellationToken cancellationToken)
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
                    await this.owner.availableItems.WaitAsync(this.cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
