namespace Concur.Tests;

using Abstractions;
using Implementations;

/// <summary>
/// Runs the shared <see cref="BoundedChannelBehaviorTests"/> contract against
/// <see cref="MpscBoundedChannel{T}"/> and adds tests for MPSC-specific guarantees.
/// </summary>
public class MpscBoundedChannelTests : BoundedChannelBehaviorTests
{
    protected override IChannel<int> CreateChannel(int capacity) =>
        new MpscBoundedChannel<int>(capacity);

    // -------------------------------------------------------------------------
    // Constructor validation
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_WithZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpscBoundedChannel<int>(capacity: 0));
    }

    [Fact]
    public void Constructor_WithNegativeCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpscBoundedChannel<int>(capacity: -1));
    }

    [Fact]
    public void Constructor_WithZeroStripeCount_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpscBoundedChannel<int>(capacity: 8, stripeCount: 0));
    }

    [Fact]
    public void Constructor_WithNegativeStripeCount_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpscBoundedChannel<int>(capacity: 8, stripeCount: -1));
    }

    // -------------------------------------------------------------------------
    // Idempotency – MPSC guarantees these; not part of the IChannel contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_CalledMultipleTimes_IsIdempotent()
    {
        // Arrange
        var channel = new MpscBoundedChannel<int>(capacity: 8);
        await channel.WriteAsync(42);

        // Act – repeated calls must not throw
        await channel.CompleteAsync();
        await channel.CompleteAsync();
        await channel.CompleteAsync();

        // Assert – item written before the first complete is still readable
        var items = await channel.ToListAsync();
        Assert.Single(items);
        Assert.Equal(42, items[0]);
    }

    [Fact]
    public async Task FailAsync_CalledMultipleTimes_UsesFirstException()
    {
        // Arrange
        var channel = new MpscBoundedChannel<int>(capacity: 8);
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");

        // Act – first FailAsync wins; subsequent calls are no-ops
        await channel.FailAsync(first);
        await channel.FailAsync(second);

        // Assert
        await using var enumerator = channel.GetAsyncEnumerator();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await enumerator.MoveNextAsync());

        Assert.Same(first, thrown);
    }

    // -------------------------------------------------------------------------
    // Single-stripe ordering
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_WithSingleStripe_PreservesInsertionOrder()
    {
        // Arrange
        var channel = new MpscBoundedChannel<int>(capacity: 16, stripeCount: 1);
        int[] values = [1, 2, 3, 4, 5, 6, 7, 8];

        foreach (var v in values)
        {
            await channel.WriteAsync(v);
        }

        await channel.CompleteAsync();

        // Act
        var collected = await channel.ToListAsync();

        // Assert – a single stripe is a strict FIFO queue
        Assert.Equal(values, collected);
    }

    // -------------------------------------------------------------------------
    // Cancellation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_WithAlreadyCancelledToken_WhenChannelFull_ThrowsOperationCanceledException()
    {
        // Arrange – capacity 1, fill it so the next write must wait
        var channel = new MpscBoundedChannel<int>(capacity: 1, stripeCount: 1);
        await channel.WriteAsync(1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => channel.WriteAsync(2, cts.Token).AsTask());
    }

    [Fact]
    public async Task GetAsyncEnumerator_WhenCancelled_StopsGracefullyWithoutException()
    {
        // Arrange – channel remains open with no items so the consumer blocks
        var channel = new MpscBoundedChannel<int>(capacity: 8);
        using var cts = new CancellationTokenSource();

        var enumerateTask = Task.Run(async () =>
        {
            var collected = new List<int>();
            await foreach (var item in channel.WithCancellation(cts.Token))
            {
                collected.Add(item);
            }

            return collected;
        });

        await channel.WriteAsync(99);
        cts.Cancel();

        // Act
        var result = await enumerateTask;

        // Assert – no exception thrown; the written item may or may not have been read
        Assert.True(result.Count is 0 or 1);
    }
}
