namespace Concur.Tests;

using System.Collections.Concurrent;
using System.Reflection;
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
    public async Task GetAsyncEnumerator_WhenCancelled_ExitsGracefullyEvenIfChannelIsFailingButNotYetDrained()
    {
        const int completionStateFailing = 2;

        var channel = new MpmcBoundedChannel<int>(capacity: 1, shardCount: 1);
        var expected = new InvalidOperationException("boom");

        SetPrivateField(channel, "completionException", expected);
        SetPrivateField(channel, "completionState", completionStateFailing);
        SetPrivateField(channel, "pendingItems", 1L);

        using var cts = new CancellationTokenSource();
        await using var enumerator = channel.GetAsyncEnumerator(cts.Token);

        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        Assert.False(moveNextTask.IsCompleted);

        cts.Cancel();

        Assert.False(await moveNextTask);
    }

    [Fact]
    public async Task WriteAsync_WhenCancellationOccursWhileBlockedOnFullChannel_ThrowsOperationCanceledException()
    {
        var channel = new MpmcBoundedChannel<int>(capacity: 1, shardCount: 1);
        await channel.WriteAsync(1);

        using var cts = new CancellationTokenSource();
        var blockedWrite = channel.WriteAsync(2, cts.Token).AsTask();

        await Task.Delay(50);
        Assert.False(blockedWrite.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedWrite);
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

    [Fact]
    public async Task WriteRead_WithHighContention_ProducersAndConsumersMuchGreaterThanShards_DrainsAllItems()
    {
        const int producers = 32;
        const int consumers = 32;
        const int perProducer = 200;

        var channel = new MpmcBoundedChannel<int>(capacity: 1024, shardCount: 4);
        var total = 0;

        var consumerTasks = Enumerable.Range(0, consumers).Select(async _ =>
        {
            await foreach (var item in channel)
            {
                Interlocked.Add(ref total, item);
            }
        }).ToArray();

        var producerTasks = Enumerable.Range(0, producers).Select(async _ =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                await channel.WriteAsync(1);
            }
        }).ToArray();

        await Task.WhenAll(producerTasks);
        await channel.CompleteAsync();
        await Task.WhenAll(consumerTasks);

        Assert.Equal(producers * perProducer, total);
    }

    [Fact]
    public async Task WriteAsync_WithConcurrentWriters_BlocksAtLogicalCapacityUntilReadsReleaseSlots()
    {
        var channel = new MpmcBoundedChannel<int>(capacity: 2, shardCount: 2);
        await channel.WriteAsync(1);
        await channel.WriteAsync(2);

        var thirdWrite = channel.WriteAsync(3).AsTask();
        var fourthWrite = channel.WriteAsync(4).AsTask();

        await Task.Delay(50);
        Assert.False(thirdWrite.IsCompleted);
        Assert.False(fourthWrite.IsCompleted);

        await using var enumerator = channel.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());

        var allWrites = Task.WhenAll(thirdWrite, fourthWrite);
        var completed = await Task.WhenAny(allWrites, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(allWrites, completed);
        Assert.True(thirdWrite.IsCompleted);
        Assert.True(fourthWrite.IsCompleted);

        await channel.CompleteAsync();
    }

    [Fact]
    public async Task FailAsync_AfterDrain_AlwaysPublishesFailureException()
    {
        const int iterations = 2_000;

        for (var i = 0; i < iterations; i++)
        {
            var channel = new MpmcBoundedChannel<int>(capacity: 1, shardCount: 1);
            var expected = new InvalidOperationException($"boom-{i}");

            await channel.WriteAsync(i);

            await using var enumerator = channel.GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(i, enumerator.Current);

            using var start = new ManualResetEventSlim(false);
            var moveNextTask = Task.Run(
                async () =>
                {
                    start.Wait();
                    return await Record.ExceptionAsync(
                        () => enumerator.MoveNextAsync().AsTask());
                });

            var failTask = Task.Run(
                async () =>
                {
                    start.Wait();
                    await channel.FailAsync(expected);
                });

            start.Set();

            await failTask;

            var exception = await moveNextTask;
            var thrown = Assert.IsType<InvalidOperationException>(exception);
            Assert.Same(expected, thrown);
        }
    }

    [Fact]
    public async Task FailAsync_WhenLastDequeueSignalsDrainDuringFailurePublication_DoesNotAllowSilentCompletion()
    {
        const int completionStateFailing = 2;
        const int completionStateFailed = 3;

        var channel = new MpmcBoundedChannel<int>(capacity: 1, shardCount: 1);
        var expected = new InvalidOperationException("boom");

        SetPrivateField(channel, "completionState", completionStateFailing);
        SignalDrainedIfCompleted(channel);

        await using var enumerator = channel.GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        Assert.False(moveNextTask.IsCompleted);

        SetPrivateField(channel, "completionException", expected);
        SetPrivateField(channel, "completionState", completionStateFailed);
        SignalDrainedIfCompleted(channel);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => moveNextTask);
        Assert.Same(expected, thrown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_WhenBlocked_TerminatesWhenChannelCompletes(bool failChannel)
    {
        var channel = new MpmcBoundedChannel<int>(capacity: 1, shardCount: 1);
        await channel.WriteAsync(1);

        var blockedWrite = channel.WriteAsync(2).AsTask();
        await Task.Delay(50);

        Assert.False(blockedWrite.IsCompleted);

        if (failChannel)
        {
            await channel.FailAsync(new InvalidOperationException("boom"));
        }
        else
        {
            await channel.CompleteAsync();
        }

        var completed = await Task.WhenAny(blockedWrite, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(blockedWrite, completed);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => blockedWrite);
    }

    [Fact]
    public async Task ReadChurn_DoesNotLeakAvailableItemPermits()
    {
        const int iterations = 4_096;

        var channel = new MpmcBoundedChannel<int>(capacity: 1, shardCount: 1);

        await using var enumerator = channel.GetAsyncEnumerator();

        for (var i = 0; i < iterations; i++)
        {
            await channel.WriteAsync(i);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(i, enumerator.Current);
        }

        Assert.Equal(0, GetAvailableItemPermitCount(channel));

        await channel.CompleteAsync();
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task DisposeEnumerator_WithBufferedPermits_DoesNotLeakItemPermits()
    {
        const int iterations = 32;

        var channel = new MpmcBoundedChannel<int>(capacity: 64, shardCount: 4);

        for (var i = 0; i < iterations; i++)
        {
            await channel.WriteAsync(i);
        }

        await using (var enumerator = channel.GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        await channel.CompleteAsync();

        var remaining = new List<int>();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var item in channel)
            {
                remaining.Add(item);
            }
        });

        var completed = await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(drainTask, completed);
        await drainTask;

        Assert.Equal(iterations - 1, remaining.Count);
        Assert.Equal(0, GetAvailableItemPermitCount(channel));
    }

    [Fact]
    public async Task MultiConsumer_WithGoAndWaitGroups_DoesNotDeadlockOnCompletion()
    {
        const int rounds = 50;
        const int producers = 8;
        const int consumers = 8;
        const int perProducer = 50_000;
        const int expectedSum = producers * perProducer;

        for (var round = 0; round < rounds; round++)
        {
            var producerWg = new WaitGroup();
            var consumerWg = new WaitGroup();
            var channel = new MpmcBoundedChannel<int>(capacity: 1024, shardCount: 4);
            long totalSum = 0;

            for (var i = 0; i < producers; i++)
            {
                ConcurRoutine.Go(
                    producerWg,
                    async ch =>
                    {
                        for (var j = 0; j < perProducer; j++)
                        {
                            await ch.WriteAsync(1);
                        }
                    },
                    channel);
            }

            ConcurRoutine.Go(async () =>
            {
                await producerWg.WaitAsync();
                await channel.CompleteAsync();
            });

            for (var i = 0; i < consumers; i++)
            {
                ConcurRoutine.Go(
                    consumerWg,
                    async ch =>
                    {
                        long local = 0;
                        await foreach (var item in ch)
                        {
                            local += item;
                        }

                        Interlocked.Add(ref totalSum, local);
                    },
                    channel);
            }

            var consumerWait = consumerWg.WaitAsync();
            var completed = await Task.WhenAny(consumerWait, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != consumerWait)
            {
                var pendingItems = GetPrivateField<long>(channel, "pendingItems");
                var availableItems = GetAvailableItemPermitCount(channel);
                var completionState = GetPrivateField<int>(channel, "completionState");

                Assert.Fail(
                    $"Potential deadlock in round {round}: pendingItems={pendingItems}, availableItems={availableItems}, completionState={completionState}.");
            }

            await consumerWait;
            Assert.Equal(expectedSum, totalSum);
        }
    }

    private static int GetAvailableItemPermitCount(MpmcBoundedChannel<int> channel)
    {
        var field = typeof(MpmcBoundedChannel<int>).GetField(
            "availableItems",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        var gate = field.GetValue(channel);
        Assert.NotNull(gate);

        var currentCountProperty = gate.GetType().GetProperty(
            "CurrentCount",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(currentCountProperty);

        var currentCount = currentCountProperty.GetValue(gate);
        Assert.NotNull(currentCount);
        return Assert.IsType<int>(currentCount);
    }

    private static TField GetPrivateField<TField>(MpmcBoundedChannel<int> channel, string fieldName)
    {
        var field = typeof(MpmcBoundedChannel<int>).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        var value = field.GetValue(channel);
        Assert.NotNull(value);
        return Assert.IsType<TField>(value);
    }

    private static void SetPrivateField<TValue>(MpmcBoundedChannel<int> channel, string fieldName, TValue value)
    {
        var field = typeof(MpmcBoundedChannel<int>).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        field.SetValue(channel, value);
    }

    private static void SignalDrainedIfCompleted(MpmcBoundedChannel<int> channel)
    {
        var method = typeof(MpmcBoundedChannel<int>).GetMethod(
            "SignalDrainedIfCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(channel, null);
    }
}
