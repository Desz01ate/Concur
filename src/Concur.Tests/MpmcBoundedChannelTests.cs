namespace Concur.Tests;

using Abstractions;
using Implementations;

public class MpmcBoundedChannelTests : BoundedChannelBehaviorTests
{
    protected override IChannel<int> CreateChannel(int capacity) =>
        new MpmcBoundedChannel<int>(capacity);

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
}
