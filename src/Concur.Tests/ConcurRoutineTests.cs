namespace Concur.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Abstractions;
using Implementations;
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
        var channel = new DefaultChannel<bool>();

        // Act
        _ = Go(async () =>
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
    public async Task Go_WithChannelFunc_WritesToChannel()
    {
        // Arrange
        var channel = new DefaultChannel<int>();
        var values = new[] { 1, 2, 3, 4, 5 };
        Func<IChannel<int>, Task> producer = async chan =>
        {
            foreach (var value in values)
            {
                await chan.WriteAsync(value);
                await Task.Delay(10);
            }

            await chan.CompleteAsync();
        };

        // Act
        _ = Go(producer, channel);

        // Assert
        var collected = new List<int>();

        await foreach (var item in channel)
        {
            collected.Add(item);

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
        var channel = new DefaultChannel<int>();
        var values = new[] { 1, 2, 3, 4, 5 };
        Func<IChannel<int>, Task> producer = async chan =>
        {
            foreach (var value in values)
            {
                await chan.WriteAsync(value);
            }
        };

        // Act
        _ = Go(producer, channel);
        _ = Go(producer, channel);
        _ = Go(producer, channel);

        // Assert
        int[] expectedResult = [..values, ..values, ..values];
        var collected = new List<int>();

        await foreach (var item in channel)
        {
            collected.Add(item);

            if (collected.Count >= expectedResult.Length)
            {
                await channel.CompleteAsync();
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
        var channelReader = Go<int>(async chan =>
            {
                foreach (var value in values)
                {
                    await chan.WriteAsync(value);
                    await Task.Delay(10);
                }

                await chan.CompleteAsync();
            },
            capacity: 10);

        // Assert
        var collected = await channelReader.ToListAsync();
        Assert.Equal(values, collected);
    }

    [Fact]
    public async Task Go_WithException_CallsOnExceptionHandler()
    {
        // Arrange
        var exceptionThrown = false;
        var originalHandler = OnException;
        var expectedException = new InvalidOperationException("Test exception");
        var channel = new DefaultChannel<Exception>();

        try
        {
            // Override the default handler for testing
            OnException = ex =>
            {
                exceptionThrown = true;
                channel.WriteAsync(ex).AsTask().Wait();
                channel.CompleteAsync().AsTask().Wait();
            };

            // Act
            _ = Go(() => throw expectedException);

            var caughtException = await channel.FirstOrDefaultAsync();

            // Assert
            Assert.NotNull(caughtException);
            Assert.True(exceptionThrown, "The exception handler was not called");
            Assert.Equal(expectedException, caughtException);
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
        var channel = new DefaultChannel<int>();
        var expectedException = new InvalidOperationException("Test exception");

        // Act
        _ = Go(async ch =>
        {
            try
            {
                await Task.Delay(10);
                throw expectedException;
            }
            catch (Exception e)
            {
                await ch.FailAsync(e);
            }
        }, channel);

        // Assert
        try
        {
            await foreach (var _ in channel)
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

    #region Sync, Generic Tests

    [Fact]
    public async Task Go_WithOneGenericParameter_ExecutesFunc()
    {
        // Arrange
        const string expectedValue = "Hello World";
        var channel = new DefaultChannel<string>();

        // Act
        Go(Producer, channel);
        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(expectedValue, result);

        static void Producer(IChannel<string> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(expectedValue).GetAwaiter().GetResult();
            ch.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithTwoGenericParameters_ExecutesFunc()
    {
        // Arrange
        const int input = 7;
        const int expectedResult = input + input;
        var channel = new DefaultChannel<int>();

        // Act
        Go(Producer, input, channel);
        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(expectedResult, result);

        static void Producer(int value, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(value + value).GetAwaiter().GetResult();
            ch.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithThreeGenericParameters_ExecutesFunc()
    {
        // Arrange
        const string input1 = "Hello";
        const string input2 = "World";
        var channel = new DefaultChannel<string>();

        // Act
        Go(Producer, input1, input2, channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal("Hello World", result);

        static void Producer(string a, string b, IChannel<string> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + " " + b).GetAwaiter().GetResult();
            ch.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithFourGenericParameters_ExecutesFunc()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act
        Go(Producer, 10, 20, 30, channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(60, result);

        static void Producer(int a, int b, int c, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + b + c).GetAwaiter().GetResult();
            ch.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithFiveGenericParameters_ExecutesFunc()
    {
        // Arrange
        var channel = new DefaultChannel<string>();

        // Act
        Go(Producer, "H", "a", "l", "l", channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal("Hall", result);

        static void Producer(string a, string b, string c, string d, IChannel<string> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + b + c + d).GetAwaiter().GetResult();
            ch.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithSixGenericParameters_ExecutesFunc()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act
        Go(Producer, 1, 2, 3, 4, 5, channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(120, result);

        static void Producer(int a, int b, int c, int d, int e, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a * b * c * d * e).GetAwaiter().GetResult();
            ch.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithSevenGenericParameters_ExecutesFunc()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act
        Go(Producer, 1, 2, 3, 4, 5, 6, channel);

        var collected = await channel.ToListAsync();

        // Assert
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, collected);

        static void Producer(int a, int b, int c, int d, int e, int f, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a).GetAwaiter().GetResult();
            ch.WriteAsync(b).GetAwaiter().GetResult();
            ch.WriteAsync(c).GetAwaiter().GetResult();
            ch.WriteAsync(d).GetAwaiter().GetResult();
            ch.WriteAsync(e).GetAwaiter().GetResult();
            ch.WriteAsync(f).GetAwaiter().GetResult();
            ch.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithEightGenericParameters_ExecutesFunc()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act
        Go(Producer, 1, 2, 3, 4, 5, 6, 7, channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(28, result);

        static void Producer(int a, int b, int c, int d, int e, int f, int g, DefaultChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + b + c + d + e + f + g).GetAwaiter().GetResult();
            ch.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    #endregion

    #region Sync, Generic WaitGroup Tests

    [Fact]
    public async Task Go_WithOneGenericParameter_AndWaitGroup_ExecutesFunc()
    {
        // Arrange
        var wg = new WaitGroup();
        var executionCount = 0;
        const string inputValue = "test string";
        var stringLengthCaptured = 0;

        // Act
        Go(wg, param =>
        {
            Thread.Sleep(10);
            Interlocked.Increment(ref executionCount);
            stringLengthCaptured = param.Length;
        }, inputValue);

        // Wait for all routines to complete
        await wg.WaitAsync();

        // Assert
        Assert.Equal(1, executionCount);
        Assert.Equal(inputValue.Length, stringLengthCaptured);
    }

    [Fact]
    public async Task Go_WithTwoGenericParameters_AndWaitGroup_ExecutesFunc()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();
        const int input = 5;

        // Act
        Go(wg, Producer, input, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(input + input, result);

        static void Producer(int a, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + a).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithThreeGenericParameters_AndWaitGroup_ExecutesFunc()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<string>();
        const string input1 = "Hello";
        const string input2 = "World";
        const string expectedResult = "Hello World";

        // Act
        Go(wg, Producer, input1, input2, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(expectedResult, result);

        static void Producer(string a, string b, IChannel<string> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + " " + b).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithFourGenericParameters_AndWaitGroup_ExecutesFunc()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        // Act
        Go(wg, Producer, 10, 20, 30, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(60, result);

        static void Producer(int a, int b, int c, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + b + c).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithFiveGenericParameters_AndWaitGroup_ExecutesFunc()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<string>();

        // Act
        Go(wg, Producer, "H", "a", "l", "l", channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal("Hall", result);

        static void Producer(string a, string b, string c, string d, IChannel<string> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + b + c + d).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithSixGenericParameters_AndWaitGroup_ExecutesFunc()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        // Act
        Go(wg, Producer, 1, 2, 3, 4, 5, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(120, result);

        static void Producer(int a, int b, int c, int d, int e, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a * b * c * d * e).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithSevenGenericParameters_AndWaitGroup_ExecutesFunc()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        // Act
        Go(wg, Producer, 1, 2, 3, 4, 5, 6, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();
        await channel.CompleteAsync();

        var result = await channel.ToListAsync();

        // Assert
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, result);

        static void Producer(int a, int b, int c, int d, int e, int f, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a).GetAwaiter().GetResult();
            ch.WriteAsync(b).GetAwaiter().GetResult();
            ch.WriteAsync(c).GetAwaiter().GetResult();
            ch.WriteAsync(d).GetAwaiter().GetResult();
            ch.WriteAsync(e).GetAwaiter().GetResult();
            ch.WriteAsync(f).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Go_WithEightGenericParameters_AndWaitGroup_ExecutesFunc()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        // Act
        Go(wg, Producer, 1, 2, 3, 4, 5, 6, 7, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(28, result);

        static void Producer(int a, int b, int c, int d, int e, int f, int g, IChannel<int> ch)
        {
            Thread.Sleep(10);
            ch.WriteAsync(a + b + c + d + e + f + g).GetAwaiter().GetResult();
        }
    }

    #endregion

    #region Async, Generic Tests

    [Fact]
    public async Task Go_WithOneGenericParameter_ExecutesTask()
    {
        // Arrange
        const string expectedValue = "Hello World";
        var channel = new DefaultChannel<string>();

        // Act
        _ = Go(Producer, channel);
        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(expectedValue, result);

        static async Task Producer(IChannel<string> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(expectedValue);
            await ch.CompleteAsync();
        }
    }

    [Fact]
    public async Task Go_WithTwoGenericParameters_ExecutesTask()
    {
        // Arrange
        const int input = 7;
        const int expectedResult = input + input;
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(Producer, input, channel);
        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(expectedResult, result);

        static async Task Producer(int value, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(value + value);
            await ch.CompleteAsync();
        }
    }

    [Fact]
    public async Task Go_WithThreeGenericParameters_ExecutesTask()
    {
        // Arrange
        const string input1 = "Hello";
        const string input2 = "World";
        var channel = new DefaultChannel<string>();

        // Act
        _ = Go(Producer, input1, input2, channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal("Hello World", result);

        static async Task Producer(string a, string b, IChannel<string> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + " " + b);
            await ch.CompleteAsync();
        }
    }

    [Fact]
    public async Task Go_WithFourGenericParameters_ExecutesTask()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(Producer, 10, 20, 30, channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(60, result);

        static async Task Producer(int a, int b, int c, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + b + c);
            await ch.CompleteAsync();
        }
    }

    [Fact]
    public async Task Go_WithFiveGenericParameters_ExecutesTask()
    {
        // Arrange
        var channel = new DefaultChannel<string>();

        // Act
        _ = Go(Producer, "H", "a", "l", "l", channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal("Hall", result);

        static async Task Producer(string a, string b, string c, string d, IChannel<string> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + b + c + d);
            await ch.CompleteAsync();
        }
    }

    [Fact]
    public async Task Go_WithSixGenericParameters_ExecutesTask()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(Producer, 1, 2, 3, 4, 5, channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(120, result);

        static async Task Producer(int a, int b, int c, int d, int e, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a * b * c * d * e);
            await ch.CompleteAsync();
        }
    }

    [Fact]
    public async Task Go_WithSevenGenericParameters_ExecutesTask()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(Producer, 1, 2, 3, 4, 5, 6, channel);

        var collected = await channel.ToListAsync();

        // Assert
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, collected);

        static async Task Producer(int a, int b, int c, int d, int e, int f, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a);
            await ch.WriteAsync(b);
            await ch.WriteAsync(c);
            await ch.WriteAsync(d);
            await ch.WriteAsync(e);
            await ch.WriteAsync(f);
            await ch.CompleteAsync();
        }
    }

    [Fact]
    public async Task Go_WithEightGenericParameters_ExecutesTask()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(Producer, 1, 2, 3, 4, 5, 6, 7, channel);

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(28, result);

        static async Task Producer(int a, int b, int c, int d, int e, int f, int g, DefaultChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + b + c + d + e + f + g);
            await ch.CompleteAsync();
        }
    }

    [Fact]
    public async Task Go_WithMultipleGenericRoutines_AllCompleteCorrectly()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act - start multiple routines with different generic parameters
        _ = Go(static async (value, ch) =>
        {
            await Task.Delay(10);
            await ch.WriteAsync(value);
        }, 1, channel);

        _ = Go(static async (a, b, ch) =>
        {
            await Task.Delay(20);
            await ch.WriteAsync(a + b);
        }, 2, 3, channel);

        _ = Go(static async (a, b, c, ch) =>
        {
            await Task.Delay(15);
            await ch.WriteAsync(a + b + c);
        }, 4, 5, 6, channel);

        // Assert
        var collected = new List<int>();

        await foreach (var item in channel)
        {
            collected.Add(item);

            if (collected.Count == 3)
            {
                await channel.CompleteAsync();
                break;
            }
        }

        Assert.Equal(3, collected.Count);
        Assert.Contains(1, collected);
        Assert.Contains(5, collected);
        Assert.Contains(15, collected);
    }

    [Fact]
    public async Task Go_WithGenericParameters_HandlesExceptions()
    {
        // Arrange
        var exceptionThrown = false;
        var originalHandler = OnException;
        var expectedException = new InvalidOperationException("Test exception");
        var channel = new DefaultChannel<Exception>();

        try
        {
            // Override the default handler for testing
            OnException = ex =>
            {
                exceptionThrown = true;
                channel.WriteAsync(ex).AsTask().Wait();
                channel.CompleteAsync().AsTask().Wait();
            };

            // Act
            _ = Go(async (_, _) =>
            {
                await Task.Delay(10);
                throw expectedException;
            }, "param1", "param2");

            var caughtException = await channel.FirstOrDefaultAsync();

            // Assert
            Assert.NotNull(caughtException);
            Assert.True(exceptionThrown, "The exception handler was not called");
            Assert.Equal(expectedException, caughtException);
        }
        finally
        {
            // Restore original handler
            OnException = originalHandler;
        }
    }

    #endregion

    #region Async, Generic WaitGroup Tests

    [Fact]
    public async Task Go_WithOneGenericParameter_AndWaitGroup_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        var executionCount = 0;
        const string inputValue = "test string";
        var stringLengthCaptured = 0;

        // Act
        _ = Go(wg, async param =>
        {
            await Task.Delay(10);
            Interlocked.Increment(ref executionCount);
            stringLengthCaptured = param.Length;
        }, inputValue);

        // Wait for all routines to complete
        await wg.WaitAsync();

        // Assert
        Assert.Equal(1, executionCount);
        Assert.Equal(inputValue.Length, stringLengthCaptured);
    }

    [Fact]
    public async Task Go_WithTwoGenericParameters_AndWaitGroup_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();
        const int input = 5;

        // Act
        _ = Go(wg, Producer, input, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(input + input, result);

        static async Task Producer(int a, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + a);
        }
    }

    [Fact]
    public async Task Go_WithThreeGenericParameters_AndWaitGroup_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<string>();
        const string input1 = "Hello";
        const string input2 = "World";
        const string expectedResult = "Hello World";

        // Act
        _ = Go(wg, Producer, input1, input2, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(expectedResult, result);

        static async Task Producer(string a, string b, IChannel<string> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + " " + b);
        }
    }

    [Fact]
    public async Task Go_WithFourGenericParameters_AndWaitGroup_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(wg, Producer, 10, 20, 30, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(60, result);

        static async Task Producer(int a, int b, int c, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + b + c);
        }
    }

    [Fact]
    public async Task Go_WithFiveGenericParameters_AndWaitGroup_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<string>();

        // Act
        _ = Go(wg, Producer, "H", "a", "l", "l", channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal("Hall", result);

        static async Task Producer(string a, string b, string c, string d, IChannel<string> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + b + c + d);
        }
    }

    [Fact]
    public async Task Go_WithSixGenericParameters_AndWaitGroup_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(wg, Producer, 1, 2, 3, 4, 5, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(120, result);

        static async Task Producer(int a, int b, int c, int d, int e, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a * b * c * d * e);
        }
    }

    [Fact]
    public async Task Go_WithSevenGenericParameters_AndWaitGroup_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(wg, Producer, 1, 2, 3, 4, 5, 6, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();
        await channel.CompleteAsync();

        var result = await channel.ToListAsync();

        // Assert
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, result);

        static async Task Producer(int a, int b, int c, int d, int e, int f, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a);
            await ch.WriteAsync(b);
            await ch.WriteAsync(c);
            await ch.WriteAsync(d);
            await ch.WriteAsync(e);
            await ch.WriteAsync(f);
        }
    }

    [Fact]
    public async Task Go_WithEightGenericParameters_AndWaitGroup_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        // Act
        _ = Go(wg, Producer, 1, 2, 3, 4, 5, 6, 7, channel);

        // Wait for all routines to complete
        await wg.WaitAsync();

        var result = await channel.FirstOrDefaultAsync();

        // Assert
        Assert.Equal(28, result);

        static async Task Producer(int a, int b, int c, int d, int e, int f, int g, IChannel<int> ch)
        {
            await Task.Delay(10);
            await ch.WriteAsync(a + b + c + d + e + f + g);
        }
    }

    [Fact]
    public async Task Go_WithMultipleGenericWaitGroupRoutines_AllCompleteCorrectly()
    {
        // Arrange
        var wg = new WaitGroup();
        var results = new ConcurrentBag<int>();

        // Act - start multiple routines with different generic parameters
        _ = Go(wg, async value =>
        {
            await Task.Delay(10);
            results.Add(value);
        }, 1);

        _ = Go(wg, async (a, b) =>
        {
            await Task.Delay(20);
            results.Add(a + b);
        }, 2, 3);

        _ = Go(wg, async (a, b, c) =>
        {
            await Task.Delay(15);
            results.Add(a + b + c);
        }, 4, 5, 6);

        // Wait for all routines to complete
        await wg.WaitAsync();

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Contains(1, results);
        Assert.Contains(5, results);
        Assert.Contains(15, results);
    }

    [Fact]
    public async Task Go_WithGenericParameters_AndWaitGroup_HandlesExceptions()
    {
        // Arrange
        var wg = new WaitGroup();
        var exceptionThrown = false;
        var originalHandler = OnException;
        var expectedException = new InvalidOperationException("Test exception");
        var channel = new DefaultChannel<Exception>();

        try
        {
            // Override the default handler for testing
            OnException = ex =>
            {
                exceptionThrown = true;
                channel.WriteAsync(ex).AsTask().Wait();
                channel.CompleteAsync().AsTask().Wait();
            };

            // Act
            _ = Go(wg, async (_, _) =>
            {
                await Task.Delay(10);
                throw expectedException;
            }, "param1", "param2");

            var caughtException = await channel.FirstOrDefaultAsync();

            // Wait for the WaitGroup to complete despite the exception
            await wg.WaitAsync();

            // Assert
            Assert.NotNull(caughtException);
            Assert.True(exceptionThrown, "The exception handler was not called");
            Assert.Equal(expectedException, caughtException);
        }
        finally
        {
            // Restore original handler
            OnException = originalHandler;
        }
    }

    #endregion
}