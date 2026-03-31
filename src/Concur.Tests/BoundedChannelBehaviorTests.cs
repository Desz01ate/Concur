namespace Concur.Tests;

using Abstractions;
using static ConcurRoutine;

/// <summary>
/// Abstract base class that defines the behavioral contract for any bounded <see cref="IChannel{T}"/>
/// implementation. Derive from this class and implement <see cref="CreateChannel"/> to have the full
/// suite run against a new backend automatically.
/// </summary>
public abstract class BoundedChannelBehaviorTests
{
    /// <summary>
    /// Creates a bounded channel with the given capacity under test.
    /// </summary>
    protected abstract IChannel<int> CreateChannel(int capacity);

    [Fact]
    public async Task Go_WithProducer_ProducesAllValues()
    {
        // Arrange
        int[] values = [1, 2, 3, 4, 5];

        // Act
        var channel = Go(
            async ch =>
            {
                foreach (var value in values)
                {
                    await ch.WriteAsync(value);
                }

                await ch.CompleteAsync();
            },
            channelFactory: () => this.CreateChannel(capacity: 16));

        // Assert
        var collected = await channel.ToListAsync();
        Assert.Equal(values.Length, collected.Count);
        Assert.Equal(values.Sum(), collected.Sum());
    }

    [Fact]
    public async Task WriteAsync_WithCapacity_BlocksWhenFull()
    {
        // Arrange
        var channel = this.CreateChannel(capacity: 1);

        // Act
        await channel.WriteAsync(1);
        var secondWrite = channel.WriteAsync(2).AsTask();
        await Task.Delay(50);

        // Assert – second write is still blocked while the buffer is full
        Assert.False(secondWrite.IsCompleted);

        var first = await channel.FirstAsync();
        Assert.Equal(1, first);

        await secondWrite;
        await channel.CompleteAsync();

        var remaining = await channel.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal(2, remaining[0]);
    }

    [Fact]
    public async Task WriteAsync_WithMultipleProducers_ReceivesAllItems()
    {
        // Arrange
        var channel = this.CreateChannel(capacity: 256);
        const int producerCount = 8;
        const int perProducer = 500;

        // Act
        var collectorTask = channel.ToListAsync();
        var producers = Enumerable.Range(0, producerCount)
                                  .Select(async producerId =>
                                  {
                                      for (var i = 0; i < perProducer; i++)
                                      {
                                          await channel.WriteAsync((producerId * perProducer) + i);
                                      }
                                  });

        await Task.WhenAll(producers);
        await channel.CompleteAsync();

        // Assert
        var items = await collectorTask;
        Assert.Equal(producerCount * perProducer, items.Count);
    }

    [Fact]
    public async Task FailAsync_PropagatesException_AfterDrainingBufferedItems()
    {
        // Arrange
        var channel = this.CreateChannel(capacity: 8);
        var expected = new InvalidOperationException("boom");

        // Act
        await channel.WriteAsync(1);
        await channel.FailAsync(expected);

        // Assert – the buffered item is still readable, then the exception surfaces
        await using var enumerator = channel.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await enumerator.MoveNextAsync());

        Assert.Same(expected, thrown);
    }

    [Fact]
    public async Task CompleteAsync_EmptyChannel_EnumerationStopsImmediately()
    {
        // Arrange
        var channel = this.CreateChannel(capacity: 8);

        // Act
        await channel.CompleteAsync();
        var items = await channel.ToListAsync();

        // Assert
        Assert.Empty(items);
    }

    [Fact]
    public async Task CompleteAsync_AllBufferedItemsDrainedBeforeEnumerationStops()
    {
        // Arrange
        var channel = this.CreateChannel(capacity: 16);
        int[] values = [10, 20, 30, 40, 50];

        foreach (var v in values)
        {
            await channel.WriteAsync(v);
        }

        await channel.CompleteAsync();

        // Act
        var collected = await channel.ToListAsync();

        // Assert – no items are lost when complete races with reads
        Assert.Equal(values.Length, collected.Count);
        Assert.Equal(values.Sum(), collected.Sum());
    }

    [Fact]
    public async Task WriteAsync_AfterCompleteAsync_ThrowsInvalidOperationException()
    {
        // Arrange
        var channel = this.CreateChannel(capacity: 8);
        await channel.CompleteAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => channel.WriteAsync(1).AsTask());
    }

    [Fact]
    public async Task WriteAsync_AfterFailAsync_ThrowsInvalidOperationException()
    {
        // Arrange
        var channel = this.CreateChannel(capacity: 8);
        await channel.FailAsync(new Exception("fail"));

        // Act & Assert
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => channel.WriteAsync(1).AsTask());
    }
}
