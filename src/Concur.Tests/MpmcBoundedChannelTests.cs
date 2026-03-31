namespace Concur.Tests;

using System.Collections.Concurrent;
using Abstractions;
using Implementations;

public class MpmcBoundedChannelTests : BoundedChannelBehaviorTests
{
    protected override IChannel<int> CreateChannel(int capacity) =>
        new MpmcBoundedChannel<int>(capacity);

    [Fact]
    public void Constructor_WithDefaultShardCount_CreatesChannel()
    {
        var channel = new MpmcBoundedChannel<int>(capacity: 8);
        Assert.NotNull(channel);
    }

    [Fact]
    public void Constructor_WithZeroCapacity_ThrowsArgumentOutOfRangeException() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpmcBoundedChannel<int>(0));

    [Fact]
    public void Constructor_WithNegativeCapacity_ThrowsArgumentOutOfRangeException() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpmcBoundedChannel<int>(-1));

    [Fact]
    public void Constructor_WithZeroShardCount_ThrowsArgumentOutOfRangeException() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpmcBoundedChannel<int>(8, 0));

    [Fact]
    public void Constructor_WithNegativeShardCount_ThrowsArgumentOutOfRangeException() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpmcBoundedChannel<int>(8, -1));

    [Fact]
    public async Task WriteAsync_WithSingleShard_PreservesInsertionOrder()
    {
        var channel = new MpmcBoundedChannel<int>(capacity: 16, shardCount: 1);
        int[] values = [1, 2, 3, 4, 5, 6, 7, 8];

        foreach (var v in values)
        {
            await channel.WriteAsync(v);
        }

        await channel.CompleteAsync();
        var collected = await channel.ToListAsync();

        Assert.Equal(values, collected);
    }

    [Fact]
    public async Task WriteAsync_WithAlreadyCancelledToken_WhenChannelFull_ThrowsOperationCanceledException()
    {
        var channel = new MpmcBoundedChannel<int>(capacity: 1, shardCount: 1);
        await channel.WriteAsync(1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => channel.WriteAsync(2, cts.Token).AsTask());
    }

    [Fact]
    public async Task WriteRead_WithMultipleProducersAndConsumers_ConsumesAllItemsExactlyOnce()
    {
        const int producers = 8;
        const int consumers = 8;
        const int perProducer = 500;

        var channel = new MpmcBoundedChannel<int>(capacity: 256, shardCount: 4);
        var bag = new ConcurrentBag<int>();

        var consumerTasks = Enumerable.Range(0, consumers).Select(async _ =>
        {
            await foreach (var item in channel)
            {
                bag.Add(item);
            }
        }).ToArray();

        var producerTasks = Enumerable.Range(0, producers).Select(async p =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                await channel.WriteAsync((p * perProducer) + i);
            }
        }).ToArray();

        await Task.WhenAll(producerTasks);
        await channel.CompleteAsync();
        await Task.WhenAll(consumerTasks);

        Assert.Equal(producers * perProducer, bag.Count);
        Assert.Equal(producers * perProducer, bag.Distinct().Count());
    }
}
