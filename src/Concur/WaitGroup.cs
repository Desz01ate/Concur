namespace Concur;

/// <summary>
/// A WaitGroup waits for a collection of routines to finish.
/// The main routine calls Add to set the number of routines to wait for.
/// Then each of the routines runs and calls Done when finished.
/// At the same time, Wait can be used to block until all routines have finished.
/// A WaitGroup must not be copied after first use.
/// </summary>
public sealed class WaitGroup
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private int count;
    private TaskCompletionSource<bool> tcs;

    /// <summary>
    /// Initializes a new instance of the <see cref="WaitGroup"/> class.
    /// </summary>
    public WaitGroup()
    {
        this.tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.tcs.SetResult(true);
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
        this.semaphore.Wait();

        try
        {
            if (this.tcs.Task.IsCompleted && delta > 0)
            {
                this.tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            this.count += delta;

            if (this.count < 0)
            {
                this.count = 0; // Reset to 0 to avoid issues with subsequent calls.
                throw new InvalidOperationException("WaitGroup counter cannot be negative.");
            }

            if (this.count == 0)
            {
                this.tcs.TrySetResult(true);
            }
        }
        finally
        {
            this.semaphore.Release();
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
    public Task WaitAsync()
    {
        this.semaphore.Wait();

        try
        {
            return this.tcs.Task;
        }
        finally
        {
            this.semaphore.Release();
        }
    }

    /// <summary>
    /// Blocks until the WaitGroup counter is zero.
    /// </summary>
    public void Wait()
    {
        this.WaitAsync().GetAwaiter().GetResult();
    }
}