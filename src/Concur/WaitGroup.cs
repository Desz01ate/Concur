namespace Concur;

/// <summary>
/// A WaitGroup waits for a collection of routines to finish.
/// The main routine calls Add to set the number of routines to wait for.
/// Then each of the routines runs and calls Done when finished.
/// At the same time, Wait can be used to block until all routines have finished.
/// A WaitGroup must not be copied after first use.
/// </summary>
/// <remarks>
/// This implementation is lock-free using an immutable state object
/// swapped atomically via <see cref="Interlocked.CompareExchange{T}"/>.
/// All operations are linearizable. Add/Done have their linearization point
/// at the successful CAS; WaitAsync/Wait linearize at the state read.
/// </remarks>
public sealed class WaitGroup
{
    private sealed class State
    {
        public readonly int Count;
        public readonly TaskCompletionSource<bool> Tcs;

        public State(int count, TaskCompletionSource<bool> tcs)
        {
            this.Count = count;
            this.Tcs = tcs;
        }
    }

    private State state;

    /// <summary>
    /// Initializes a new instance of the <see cref="WaitGroup"/> class.
    /// </summary>
    public WaitGroup()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        this.state = new State(0, tcs);
    }

    /// <summary>
    /// Adds a delta to the WaitGroup counter.
    /// If the counter becomes zero, all routines blocked on Wait are released.
    /// If the counter goes negative, it panics.
    /// </summary>
    /// <param name="delta">The number of routines to add to the wait group.</param>
    /// <exception cref="InvalidOperationException">Thrown when the counter goes negative.</exception>
    public void Add(int delta)
    {
        SpinWait spin = default;

        while (true)
        {
            var current = this.state;
            var newCount = current.Count + delta;

            if (newCount < 0)
            {
                throw new InvalidOperationException("WaitGroup counter cannot be negative.");
            }

            State next;

            if (current.Count == 0 && delta > 0)
            {
                var freshTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                next = new State(newCount, freshTcs);
            }
            else
            {
                next = new State(newCount, current.Tcs);
            }

            if (Interlocked.CompareExchange(ref this.state, next, current) == current)
            {
                if (newCount == 0)
                {
                    current.Tcs.TrySetResult(true);
                }

                return;
            }

            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Decrements the WaitGroup counter by one.
    /// </summary>
    public void Done()
    {
        this.Add(-1);
    }

    /// <summary>
    /// Blocks until the WaitGroup counter is zero asynchronously.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var task = this.state.Tcs.Task;

        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            return task;
        }

        return WaitAsyncCore(task, cancellationToken);
    }

    /// <summary>
    /// Blocks until the WaitGroup counter is zero.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public void Wait(CancellationToken cancellationToken = default)
    {
        this.WaitAsync(cancellationToken).GetAwaiter().GetResult();
    }
    private static async Task WaitAsyncCore(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

}
