namespace Concur.Tests;

using Implementations;
using static ConcurRoutine;

public class WaitGroupTests
{
    [Fact]
    public async Task Go_WithWaitGroup_WithAction_ExecutesAction()
    {
        // Arrange
        var wg = new WaitGroup();
        bool[] executeResults = [false, false, false];

        // Act
        Go(wg, () =>
        {
            Thread.Sleep(100);
            executeResults[0] = true;
        });
        Go(wg, () =>
        {
            Thread.Sleep(500);
            executeResults[1] = true;
        });
        Go(wg, () =>
        {
            Thread.Sleep(1000);
            executeResults[2] = true;
        });

        await wg.WaitAsync();

        // Assert
        Assert.All(executeResults, Assert.True);
    }

    [Fact]
    public async Task Go_WithWaitGroup_WithTaskFunc_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        bool[] executeResults = [false, false, false];

        // Act
        Go(wg, async () =>
        {
            await Task.Delay(100);
            executeResults[0] = true;
        });
        Go(wg, async () =>
        {
            await Task.Delay(500);
            executeResults[1] = true;
        });
        Go(wg, async () =>
        {
            await Task.Delay(1000);
            executeResults[2] = true;
        });

        await wg.WaitAsync();

        // Assert
        Assert.All(executeResults, Assert.True);
    }

    [Fact]
    public async Task Go_WithWaitGroup_WithChannelFunc_WritesToChannel()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();
        var values = new[] { 1, 2, 3, 4, 5 };
        Func<DefaultChannel<int>, Task> producer = async chan =>
        {
            foreach (var value in values)
            {
                await chan.WriteAsync(value);
                await Task.Delay(10);
            }
        };

        // Act
        Go(wg, producer, channel);
        Go(wg, producer, channel);
        Go(wg, producer, channel);

        await wg.WaitAsync();
        await channel.CompleteAsync();

        // Assert
        int[] expectedResult = [..values, ..values, ..values];
        var collected = new List<int>();

        await foreach (var item in channel)
        {
            collected.Add(item);

            if (collected.Count >= expectedResult.Length)
            {
                break;
            }
        }

        Assert.Equivalent(expectedResult, collected);
    }

    [Fact]
    public void Add_WithNegativeDelta_ThrowsInvalidOperationException()
    {
        // Arrange
        var wg = new WaitGroup();

        // Act & Assert
        var exception = Record.Exception(() => wg.Add(-1));
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("WaitGroup counter cannot be negative.", exception.Message);
    }

    [Fact]
    public void WaitAsync_CompletesImmediately_WhenCountIsZero()
    {
        // Arrange
        var wg = new WaitGroup(); // Initially count is 0

        // Act
        var waitTask = wg.WaitAsync();

        // Assert
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitAsync_CompletesAfterDone_WhenCountReachesZero()
    {
        // Arrange
        var wg = new WaitGroup();
        wg.Add(1); // Increment count to 1

        // Act
        var waitTask = wg.WaitAsync();
        Assert.False(waitTask.IsCompleted); // Should not be completed yet

        wg.Done(); // Decrement count to 0

        // Assert
        await waitTask; // Wait for the task to complete
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitAsync_WithWaitGroupReuse_WaitsForAllWork()
    {
        // Verifies that WaitAsync correctly waits when the WaitGroup is reused
        // across multiple batches. The race: when Done() brings count to 0 and
        // completes tcs, a subsequent Add(1) creates a new tcs. Without
        // synchronized reads, WaitAsync() could read the old completed tcs
        // and return before the new batch finishes.

        var failures = 0;
        var iterations = 1000;

        for (var i = 0; i < iterations; i++)
        {
            var wg = new WaitGroup();
            var completed = 0;
            var routineCount = 10;

            // Batch 1: add and complete to put tcs into completed state
            wg.Add(1);
            wg.Done();

            // Batch 2: add work using the correct Go pattern (Add before Task.Run)
            for (var j = 0; j < routineCount; j++)
            {
                wg.Add(1);

                _ = Task.Run(() =>
                {
                    Thread.SpinWait(100);
                    Interlocked.Increment(ref completed);
                    wg.Done();
                });
            }

            await wg.WaitAsync();

            if (Volatile.Read(ref completed) < routineCount)
            {
                failures++;
            }
        }

        Assert.True(failures == 0,
            $"WaitAsync() returned before all routines completed in {failures}/{iterations} iterations. " +
            "This indicates a race condition where WaitAsync() reads a stale (already completed) TaskCompletionSource.");
    }

    [Fact]
    public async Task WaitAsync_WithConcurrentAddAndWait_DoesNotReturnPrematurely()
    {
        // Tests the ConcurRoutine.Go pattern under high concurrency:
        // Add(1) is called synchronously before Task.Run, then WaitAsync
        // must correctly wait for all routines to complete.

        var failures = 0;
        var iterations = 1000;

        for (var i = 0; i < iterations; i++)
        {
            var wg = new WaitGroup();
            var completed = 0;
            var routineCount = 20;

            for (var j = 0; j < routineCount; j++)
            {
                wg.Add(1);

                _ = Task.Run(() =>
                {
                    Thread.SpinWait(100);
                    Interlocked.Increment(ref completed);
                    wg.Done();
                });
            }

            await wg.WaitAsync();

            if (Volatile.Read(ref completed) < routineCount)
            {
                failures++;
            }
        }

        Assert.True(failures == 0,
            $"WaitAsync() returned before all routines completed in {failures}/{iterations} iterations.");
    }
}