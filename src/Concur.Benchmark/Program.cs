using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using Concur;
using Concur.Implementations;
using static Concur.ConcurRoutine;

internal static class BenchmarkConfig
{
    public static readonly int Iterations =
        GetPositiveIntFromEnvironment("CONCUR_BENCH_ITERATIONS", 200_000);
    public static readonly int Concurrency =
        GetPositiveIntFromEnvironment("CONCUR_BENCH_CONCURRENCY", Environment.ProcessorCount);
    public static readonly int ConsumerCount =
        GetPositiveIntFromEnvironment("CONCUR_BENCH_CONSUMER_COUNT", Concurrency);
    public static readonly int ChannelCapacity =
        GetPositiveIntFromEnvironment("CONCUR_BENCH_CHANNEL_CAPACITY", 16_384);
    public static readonly long ExpectedSum = (long)Iterations * Concurrency;

    public readonly static BoundedChannelOptions SingleConsumerChannelOptions = new(ChannelCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    };

    public readonly static BoundedChannelOptions MultiConsumerChannelOptions = new(ChannelCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
    };

    public static void EnsureExpectedSum(long actualSum)
    {
        if (actualSum != ExpectedSum)
        {
            throw new Exception("Sum is not correct");
        }
    }

    private static int GetPositiveIntFromEnvironment(string variableName, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out var parsedValue) && parsedValue > 0)
        {
            return parsedValue;
        }

        return defaultValue;
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[DisassemblyDiagnoser(printSource: true, maxDepth: 2)]
[IterationCount(10)]
[WarmupCount(5)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class SingleConsumerBenchmark
{
    [Benchmark]
    public async Task Goroutine_WithWaitGroup()
    {
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>(BenchmarkConfig.SingleConsumerChannelOptions);

        for (var i = 0; i < BenchmarkConfig.Concurrency; i++)
        {
            Go(wg, async ch =>
            {
                for (var j = 0; j < BenchmarkConfig.Iterations; j++)
                {
                    await ch.WriteAsync(1);
                }
            }, channel);
        }

        Go(async () =>
        {
            await wg.WaitAsync();
            await channel.CompleteAsync();
        });

        var sum = await channel.SumAsync();
        BenchmarkConfig.EnsureExpectedSum(sum);
    }

    [Benchmark]
    public async Task MpscBoundedChannel_WithWaitGroup()
    {
        var wg = new WaitGroup();
        var channel = new MpscBoundedChannel<int>(capacity: BenchmarkConfig.ChannelCapacity, stripeCount: 4);

        for (var i = 0; i < BenchmarkConfig.Concurrency; i++)
        {
            Go(wg, async ch =>
            {
                for (var j = 0; j < BenchmarkConfig.Iterations; j++)
                {
                    await ch.WriteAsync(1);
                }
            }, channel);
        }

        Go(async () =>
        {
            await wg.WaitAsync();
            await channel.CompleteAsync();
        });

        var sum = await channel.SumAsync();
        BenchmarkConfig.EnsureExpectedSum(sum);
    }

    [Benchmark]
    public async Task MpmcBoundedChannel_WithWaitGroup()
    {
        var wg = new WaitGroup();
        var channel = new MpmcBoundedChannel<int>(capacity: BenchmarkConfig.ChannelCapacity, shardCount: 4);

        for (var i = 0; i < BenchmarkConfig.Concurrency; i++)
        {
            Go(wg, async ch =>
            {
                for (var j = 0; j < BenchmarkConfig.Iterations; j++)
                {
                    await ch.WriteAsync(1);
                }
            }, channel);
        }

        Go(async () =>
        {
            await wg.WaitAsync();
            await channel.CompleteAsync();
        });

        var sum = await channel.SumAsync();
        BenchmarkConfig.EnsureExpectedSum(sum);
    }

    [Benchmark(Baseline = true)]
    public async Task Channel_WithTpl()
    {
        var channel = Channel.CreateBounded<int>(BenchmarkConfig.SingleConsumerChannelOptions);
        var writer = channel.Writer;
        var reader = channel.Reader;

        var producerTasks = new List<Task>();

        for (var i = 0; i < BenchmarkConfig.Concurrency; i++)
        {
            producerTasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < BenchmarkConfig.Iterations; j++)
                {
                    await writer.WriteAsync(1);
                }
            }));
        }

        var completionTask = Task.Run(async () =>
        {
            await Task.WhenAll(producerTasks);
            writer.Complete();
        });

        var sum = await reader.ReadAllAsync().SumAsync();

        await completionTask;
        BenchmarkConfig.EnsureExpectedSum(sum);
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[DisassemblyDiagnoser(printSource: true, maxDepth: 2)]
[IterationCount(10)]
[WarmupCount(5)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class MultiConsumerBenchmark
{
    [Benchmark]
    public async Task MpmcBoundedChannel_MultiConsumer()
    {
        await RunMpmcMultiConsumerWithGoAsync(shardCount: 4);
    }

    [Benchmark]
    public async Task MpmcBoundedChannel_MultiConsumer_Shards1()
    {
        await RunMpmcMultiConsumerWithGoAsync(shardCount: 1);
    }

    [Benchmark]
    public async Task MpmcBoundedChannel_MultiConsumer_Shards8()
    {
        await RunMpmcMultiConsumerWithGoAsync(shardCount: 8);
    }

    [Benchmark]
    public async Task MpmcBoundedChannel_MultiConsumer_TaskRun()
    {
        var channel = new MpmcBoundedChannel<int>(capacity: BenchmarkConfig.ChannelCapacity, shardCount: 4);
        long totalSum = 0;

        var producerTasks = new List<Task>(BenchmarkConfig.Concurrency);
        for (var i = 0; i < BenchmarkConfig.Concurrency; i++)
        {
            producerTasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < BenchmarkConfig.Iterations; j++)
                {
                    await channel.WriteAsync(1);
                }
            }));
        }

        var completionTask = Task.Run(async () =>
        {
            await Task.WhenAll(producerTasks);
            await channel.CompleteAsync();
        });

        var consumerTasks = new List<Task>(BenchmarkConfig.ConsumerCount);
        for (var i = 0; i < BenchmarkConfig.ConsumerCount; i++)
        {
            consumerTasks.Add(Task.Run(async () =>
            {
                long localSum = 0;
                await foreach (var item in channel)
                {
                    localSum += item;
                }

                Interlocked.Add(ref totalSum, localSum);
            }));
        }

        await Task.WhenAll(consumerTasks);
        await completionTask;

        BenchmarkConfig.EnsureExpectedSum(totalSum);
    }

    [Benchmark(Baseline = true)]
    public async Task Channel_WithTpl_MultiConsumer()
    {
        var channel = Channel.CreateBounded<int>(BenchmarkConfig.MultiConsumerChannelOptions);
        var writer = channel.Writer;
        var reader = channel.Reader;
        long totalSum = 0;

        var producerTasks = new List<Task>();

        for (var i = 0; i < BenchmarkConfig.Concurrency; i++)
        {
            producerTasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < BenchmarkConfig.Iterations; j++)
                {
                    await writer.WriteAsync(1);
                }
            }));
        }

        var completionTask = Task.Run(async () =>
        {
            await Task.WhenAll(producerTasks);
            writer.Complete();
        });

        var consumerTasks = new List<Task>();

        for (var i = 0; i < BenchmarkConfig.ConsumerCount; i++)
        {
            consumerTasks.Add(Task.Run(async () =>
            {
                long localSum = 0;
                await foreach (var item in reader.ReadAllAsync())
                {
                    localSum += item;
                }

                Interlocked.Add(ref totalSum, localSum);
            }));
        }

        await Task.WhenAll(consumerTasks);
        await completionTask;

        BenchmarkConfig.EnsureExpectedSum(totalSum);
    }

    private static async Task RunMpmcMultiConsumerWithGoAsync(int shardCount)
    {
        var producerWg = new WaitGroup();
        var consumerWg = new WaitGroup();
        var channel = new MpmcBoundedChannel<int>(capacity: BenchmarkConfig.ChannelCapacity, shardCount: shardCount);
        long totalSum = 0;

        for (var i = 0; i < BenchmarkConfig.Concurrency; i++)
        {
            Go(producerWg, async ch =>
            {
                for (var j = 0; j < BenchmarkConfig.Iterations; j++)
                {
                    await ch.WriteAsync(1);
                }
            }, channel);
        }

        Go(async () =>
        {
            await producerWg.WaitAsync();
            await channel.CompleteAsync();
        });

        for (var i = 0; i < BenchmarkConfig.ConsumerCount; i++)
        {
            Go(consumerWg, async ch =>
            {
                long localSum = 0;
                await foreach (var item in ch)
                {
                    localSum += item;
                }

                Interlocked.Add(ref totalSum, localSum);
            }, channel);
        }

        await consumerWg.WaitAsync();
        BenchmarkConfig.EnsureExpectedSum(totalSum);
    }
}

class Program
{
    static void Main(string[] args)
    {
        BenchmarkSwitcher.FromTypes([typeof(SingleConsumerBenchmark), typeof(MultiConsumerBenchmark)]).Run(args);
    }
}
