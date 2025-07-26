namespace Concur.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Concur.Handlers;
using Handlers;
using Xunit;
using static ConcurRoutine;

public class ExceptionHandlingTests
{
    [Fact]
    public async Task SilentExceptionHandler_IgnoresExceptions()
    {
        // Arrange
        var options = new GoOptions
        {
            ExceptionHandler = SilentExceptionHandler.Instance,
            OperationName = "SilentTest",
        };
        var resetEvent = new ManualResetEventSlim(false);

        // Act
        Go(() =>
        {
            resetEvent.Set();
            throw new InvalidOperationException("This should be ignored");
        }, options);

        var signaled = resetEvent.Wait(TimeSpan.FromSeconds(1));

        // Assert - should complete without any exceptions being propagated
        Assert.True(signaled);
    }

    [Fact]
    public async Task GoOptions_WithMetadata_PassesMetadataToHandler()
    {
        // Arrange
        var testHandler = new TestExceptionHandler();
        var resetEvent = new ManualResetEventSlim(false);
        var metadata = new Dictionary<string, object?>
        {
            ["UserId"] = 123,
            ["OperationType"] = "DataProcessing",
            ["RequestId"] = Guid.NewGuid(),
        };

        var options = new GoOptions
        {
            ExceptionHandler = new TestChannelExceptionHandler(testHandler, resetEvent),
            OperationName = "MetadataTest",
            Metadata = metadata,
        };

        // Act
        Go(() => throw new ArgumentException("Test with metadata"), options);

        var signaled = resetEvent.Wait(TimeSpan.FromSeconds(1));

        // Assert
        Assert.True(signaled);
        var capturedExceptions = testHandler.GetCapturedExceptions();
        Assert.Single(capturedExceptions);

        var context = capturedExceptions[0];
        Assert.Equal("MetadataTest", context.OperationName);
        Assert.Equal(123, context.Metadata["UserId"]);
        Assert.Equal("DataProcessing", context.Metadata["OperationType"]);
        Assert.Contains("RequestId", context.Metadata.Keys);
    }

    [Fact]
    public async Task ExceptionContext_ContainsTimestampAndRoutineId()
    {
        // Arrange
        var testHandler = new TestExceptionHandler();
        var resetEvent = new ManualResetEventSlim(false);
        var startTime = DateTimeOffset.UtcNow;

        var options = new GoOptions
        {
            ExceptionHandler = new TestChannelExceptionHandler(testHandler, resetEvent),
        };

        // Act
        Go(() => throw new InvalidOperationException("Timestamp test"), options);

        var signaled = resetEvent.Wait(TimeSpan.FromSeconds(1));
        var endTime = DateTimeOffset.UtcNow;

        // Assert
        Assert.True(signaled);
        var capturedExceptions = testHandler.GetCapturedExceptions();
        Assert.Single(capturedExceptions);

        var context = capturedExceptions[0];
        Assert.True(context.Timestamp >= startTime && context.Timestamp <= endTime);
        Assert.NotEmpty(context.RoutineId);
        Assert.True(context.RoutineId.Length == 8); // Should be 8-character GUID prefix
    }

    [Fact]
    public async Task Channel_WithException_CallsHandlerAndFailsChannel()
    {
        // Arrange
        var testHandler = new TestExceptionHandler();
        var resetEvent = new ManualResetEventSlim(false);
        var expectedException = new InvalidOperationException("Channel test exception");

        var options = new GoOptions
        {
            ExceptionHandler = new TestChannelExceptionHandler(testHandler, resetEvent),
            OperationName = "ChannelTest",
        };

        // Act
        var channel = Go<int>(async ch =>
        {
            await ch.WriteAsync(1);
            throw expectedException;
        }, options: options);

        var signaled = resetEvent.Wait(TimeSpan.FromSeconds(1));

        // Assert
        Assert.True(signaled);
        var capturedExceptions = testHandler.GetCapturedExceptions();
        Assert.Single(capturedExceptions);

        var context = capturedExceptions[0];
        Assert.Equal(expectedException, context.Exception);
        Assert.Equal("ChannelTest", context.OperationName);

        // Channel should be failed
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in channel)
            {
                // Should throw the exception
            }
        });
    }

    [Fact]
    public async Task WaitGroup_WithException_CompletesWaitGroup()
    {
        // Arrange
        var wg = new WaitGroup();
        var testHandler = new TestExceptionHandler();
        var resetEvent = new ManualResetEventSlim(false);

        var options = new GoOptions
        {
            ExceptionHandler = new TestChannelExceptionHandler(testHandler, resetEvent),
            OperationName = "WaitGroupExceptionTest",
        };

        // Act
        Go(wg, () => throw new InvalidOperationException("WaitGroup exception"), options);

        var signaled = resetEvent.Wait(TimeSpan.FromSeconds(1));
        await wg.WaitAsync(); // Should complete despite exception

        // Assert
        Assert.True(signaled);
        var capturedExceptions = testHandler.GetCapturedExceptions();
        Assert.Single(capturedExceptions);
        Assert.Equal("WaitGroupExceptionTest", capturedExceptions[0].OperationName);
    }

    [Fact]
    public async Task ExceptionHandlerException_DoesNotPropagate()
    {
        // Arrange
        var faultyHandler = new FaultyExceptionHandler();
        var resetEvent = new ManualResetEventSlim(false);

        var options = new GoOptions
        {
            ExceptionHandler = faultyHandler,
        };

        // Act & Assert - should not throw even though handler throws
        Go(() =>
        {
            resetEvent.Set();
            throw new InvalidOperationException("Original exception");
        }, options);

        var signaled = resetEvent.Wait(TimeSpan.FromSeconds(1));
        Assert.True(signaled);

        // Test should complete without the handler exception propagating
    }

    [Fact]
    public async Task AggregateExceptionHandler_AggregatesExceptionsCorrectly()
    {
        // Arrange
        var wg = new WaitGroup();
        var aggregateHandler = new AggregateExceptionHandler();
        var exceptionsToThrow = new List<Exception>
        {
            new InvalidOperationException("First error"),
            new ArgumentNullException("Second error"),
            new TaskCanceledException("Third error"),
        };

        var options = new GoOptions
        {
            ExceptionHandler = aggregateHandler,
        };

        // Act
        foreach (var ex in exceptionsToThrow)
        {
            Go(wg, () => throw ex, options);
        }

        await wg.WaitAsync();

        // Assert
        var aggregateException = aggregateHandler.GetAggregateException();
        Assert.Equal(exceptionsToThrow.Count, aggregateException.InnerExceptions.Count);

        foreach (var ex in exceptionsToThrow)
        {
            Assert.Contains(ex, aggregateException.InnerExceptions);
        }
    }
}