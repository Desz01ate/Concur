namespace Concur.Tests;

using Channels;
using static ConcurRoutine;

/// <summary>
/// Verify the behavior of an QueueChannel<> with custom implementation based on ConcurrentQueue.
/// Ensures that it works with the `Go` function regardless of the implementation, assuming it's implemented correctly.
/// </summary>
public class QueueChannelBehaviorTests
{
    [Fact]
    public async Task Go_WithQueueChannelUnbounded_WithAction_WithClosure_ExecutesFunc()
    {
        // Arrange
        var executed = false;
        var channel = new QueueChannel<bool>();

        // Act
        Go(() =>
        {
            Thread.Sleep(10);
            executed = true;
            channel.WriteAsync(true).AsTask().Wait();
            channel.CompleteAsync().AsTask().Wait();
        });

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.True(result, "No result was received from the channel");
        Assert.True(executed, "The task function was not executed");
    }

    [Fact]
    public async Task Go_WithQueueChannelUnbounded_WithTaskFunc_ExecutesTask()
    {
        // Arrange
        var executed = false;
        QueueChannel<bool> channel = new QueueChannel<bool>();

        // Act
        Go(async () =>
        {
            await Task.Delay(10);
            executed = true;
            await channel.WriteAsync(true);
            await channel.CompleteAsync();
        });

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.True(result, "No result was received from the channel");
        Assert.True(executed, "The task function was not executed");
    }

    [Fact]
    public async Task Go_WithQueueChannelUnbounded_WithMultipleRoutines_AllCompleteCorrectly()
    {
        // Arrange
        QueueChannel<int> channel = new QueueChannel<int>();
        const int concurrentTasks = 3;

        // Act
        Go(async ch =>
        {
            await Task.Delay(100);
            await ch.WriteAsync(1);
        }, channel);
        Go(async ch =>
        {
            await Task.Delay(500);
            await ch.WriteAsync(1);
        }, channel);
        Go(async ch =>
        {
            await Task.Delay(1000);
            await ch.WriteAsync(1);
        }, channel);

        var sum = 0;

        await foreach (var item in channel)
        {
            sum += item;

            if (sum >= concurrentTasks)
            {
                break;
            }
        }

        // Assert
        Assert.Equal(concurrentTasks, sum);
    }

    [Fact]
    public async Task Go_WithQueueChannelUnbounded_WithMultipleRoutines_WithWaitGroup_AllCompleteCorrectly()
    {
        // Arrange
        QueueChannel<int> channel = new QueueChannel<int>();
        var wg = new WaitGroup();
        const int concurrentTasks = 3;

        // Act
        Go(wg, async ch =>
        {
            await Task.Delay(100);
            await ch.WriteAsync(1);
        }, channel);
        Go(wg, async ch =>
        {
            await Task.Delay(500);
            await ch.WriteAsync(1);
        }, channel);
        Go(wg, async ch =>
        {
            await Task.Delay(1000);
            await ch.WriteAsync(1);
        }, channel);

        await wg.WaitAsync();
        await channel.CompleteAsync();

        var sum = await channel.SumAsync();

        // Assert
        Assert.Equal(concurrentTasks, sum);
    }

    [Fact]
    public async Task Go_WithQueueChannelBounded_WithAction_WithClosure_ExecutesFunc()
    {
        // Arrange
        var executed = false;
        QueueChannel<bool> channel = new QueueChannel<bool>(1);

        // Act
        Go(() =>
        {
            Thread.Sleep(10);
            executed = true;
            channel.WriteAsync(true).AsTask().Wait();
            channel.CompleteAsync().AsTask().Wait();
        });

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.True(result, "No result was received from the channel");
        Assert.True(executed, "The task function was not executed");
    }

    [Fact]
    public async Task Go_WithQueueChannelBounded_WithTaskFunc_ExecutesTask()
    {
        // Arrange
        var executed = false;
        QueueChannel<bool> channel = new QueueChannel<bool>(1);

        // Act
        Go(async () =>
        {
            await Task.Delay(10);
            executed = true;
            await channel.WriteAsync(true);
            await channel.CompleteAsync();
        });

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.True(result, "No result was received from the channel");
        Assert.True(executed, "The task function was not executed");
    }

    [Fact]
    public async Task Go_WithQueueChannelBounded_WithMultipleRoutines_AllCompleteCorrectly()
    {
        // Arrange
        QueueChannel<int> channel = new QueueChannel<int>(1);
        const int concurrentTasks = 3;

        // Act
        Go(async ch =>
        {
            await Task.Delay(100);
            await ch.WriteAsync(1);
        }, channel);
        Go(async ch =>
        {
            await Task.Delay(500);
            await ch.WriteAsync(1);
        }, channel);
        Go(async ch =>
        {
            await Task.Delay(1000);
            await ch.WriteAsync(1);
        }, channel);

        var sum = 0;

        await foreach (var item in channel)
        {
            sum += item;

            if (sum >= concurrentTasks)
            {
                break;
            }
        }

        // Assert
        Assert.Equal(concurrentTasks, sum);
    }

    [Fact]
    public async Task Go_WithQueueChannelBounded_WithMultipleRoutines_WithWaitGroup_AllCompleteCorrectly()
    {
        // Arrange
        // the queue channel having a few caveat when using as a bounded mode,
        // making it deadlock as a result when the consumer is higher than the buffer size.
        QueueChannel<int> channel = new QueueChannel<int>(3);
        var wg = new WaitGroup();
        const int concurrentTasks = 3;

        // Act
        Go(wg, async ch =>
        {
            await Task.Delay(100);
            await ch.WriteAsync(1);
        }, channel);
        Go(wg, async ch =>
        {
            await Task.Delay(500);
            await ch.WriteAsync(1);
        }, channel);
        Go(wg, async ch =>
        {
            await Task.Delay(1000);
            await ch.WriteAsync(1);
        }, channel);

        await wg.WaitAsync();
        await channel.CompleteAsync();

        var sum = await channel.SumAsync();

        // Assert
        Assert.Equal(concurrentTasks, sum);
    }
}