namespace Concur.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;
using static ConcurRoutine;

public class ConcurRoutineTests
{
    [Fact]
    public async Task Go_WithAction_ExecutesAction()
    {
        // Arrange
        var executed = false;
        var resetEvent = new ManualResetEventSlim(false);

        // Act
        Go(() =>
        {
            executed = true;
            resetEvent.Set();
        });

        // Assert
        var signaled = resetEvent.Wait(TimeSpan.FromSeconds(1));
        Assert.True(signaled, "The action was not executed within the timeout");
        Assert.True(executed, "The action was not executed");
    }

    [Fact]
    public async Task Go_WithTaskFunc_ExecutesTask()
    {
        // Arrange
        var executed = false;
        var tcs = new TaskCompletionSource<bool>();

        // Act
        _ = Go(async () =>
        {
            await Task.Delay(10);
            executed = true;
            tcs.SetResult(true);
        });

        // Wait with timeout
        var result = await Task.WhenAny(tcs.Task, Task.Delay(1000));

        // Assert
        Assert.Equal(tcs.Task, result);
        Assert.True(executed, "The task function was not executed");
    }

    [Fact]
    public async Task Go_WithChannelFunc_WritesToChannel()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<int>();
        var values = new[] { 1, 2, 3, 4, 5 };
        Func<ChannelWriter<int>, Task> producer = async writer =>
        {
            foreach (var value in values)
            {
                await writer.WriteAsync(value);
                await Task.Delay(10);
            }

            writer.TryComplete();
        };

        // Act
        _ = Go(producer, channel.Writer);

        // Assert
        var collected = new List<int>();

        while (await channel.Reader.WaitToReadAsync())
        {
            while (channel.Reader.TryRead(out var item))
            {
                collected.Add(item);
            }

            if (collected.Count >= values.Length)
            {
                break;
            }
        }

        Assert.Equal(values, collected);
    }

    [Fact]
    public async Task Go_WithMultipleRoutines_WithChannelFunc_WritesToChannel()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<int>();
        var values = new[] { 1, 2, 3, 4, 5 };
        Func<ChannelWriter<int>, Task> producer = async writer =>
        {
            foreach (var value in values)
            {
                await writer.WriteAsync(value);
            }
        };

        // Act
        _ = Go(producer, channel.Writer);
        _ = Go(producer, channel.Writer);
        _ = Go(producer, channel.Writer);

        // Assert
        int[] expectedResult = [..values, ..values, ..values];
        var collected = new List<int>();

        while (await channel.Reader.WaitToReadAsync())
        {
            while (channel.Reader.TryRead(out var item))
            {
                collected.Add(item);
            }
            
            if (collected.Count >= expectedResult.Length)
            {
                channel.Writer.TryComplete();
                break;
            }
        }

        Assert.Equivalent(expectedResult, collected);
    }

    [Fact]
    public async Task Go_WithChannelCreation_CreatesChannelAndProducesValues()
    {
        // Arrange
        var values = new[] { 1, 2, 3, 4, 5 };

        // Act
        var channelReader = Go<int>(async writer =>
        {
            foreach (var value in values)
            {
                await writer.WriteAsync(value);
                await Task.Delay(10);
            }
            
            writer.TryComplete();
        }, capacity: 10);

        // Assert
        var collected = await channelReader.ReadAllAsync().ToListAsync();
        Assert.Equal(values, collected);
    }

    [Fact]
    public async Task Go_WithException_CallsOnExceptionHandler()
    {
        // Arrange
        var exceptionThrown = false;
        var originalHandler = OnException;
        var expectedException = new InvalidOperationException("Test exception");
        var tcs = new TaskCompletionSource<Exception>();

        try
        {
            // Override the default handler for testing
            OnException = ex =>
            {
                exceptionThrown = true;
                tcs.TrySetResult(ex);
            };

            // Act
            _ = Go(() => throw expectedException);

            // Wait for the exception handler to be called with timeout
            var task = await Task.WhenAny(tcs.Task, Task.Delay(1000));

            // Assert
            Assert.Equal(tcs.Task, task);
            Assert.True(exceptionThrown, "The exception handler was not called");
            Assert.Equal(expectedException, await tcs.Task);
        }
        finally
        {
            // Restore original handler
            OnException = originalHandler;
        }
    }

    [Fact]
    public async Task Go_WithChannelAndException_CompletesChannelWithError()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<int>();
        var expectedException = new InvalidOperationException("Test exception");

        // Act
        _ = Go(async _ =>
        {
            await Task.Delay(10);
            throw expectedException;
        }, channel.Writer);

        // Assert
        try
        {
            await foreach (var _ in channel.Reader.ReadAllAsync())
            {
                // Should not get here
                Assert.Fail("Channel should be completed with error");
            }

            // Should not get here
            Assert.Fail("Channel should be completed with error");
        }
        catch (Exception ex)
        {
            Assert.Equal(expectedException, ex);
        }
    }
}