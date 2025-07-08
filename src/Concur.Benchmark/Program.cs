using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using Concur;
using Concur.Implementations;
using static Concur.ConcurRoutine;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[DisassemblyDiagnoser(printSource: true, maxDepth: 2)]
[IterationCount(100)]
[WarmupCount(10)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class GoBenchmark
{
    private const int iterations = 100_000;
    private const int concurrency = 10;
    private const int expectedSum = iterations * concurrency;

    [Benchmark]
    public async Task Goroutine()
    {
        var channel = new DefaultChannel<int>();

        for (var i = 0; i < concurrency; i++)
        {
            _ = Go(async ch =>
            {
                for (var j = 0; j < iterations; j++)
                {
                    await ch.WriteAsync(1);
                }
            }, channel);
        }

        var sum = 0;

        await foreach (var item in channel)
        {
            sum += item;

            if (sum >= expectedSum)
            {
                break;
            }
        }

        await channel.CompleteAsync();

        if (sum != expectedSum)
        {
            throw new Exception("Sum is not correct");
        }
    }

    [Benchmark]
    public async Task Goroutine_WithWaitGroup()
    {
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        for (var i = 0; i < concurrency; i++)
        {
            _ = Go(wg, async ch =>
            {
                for (var j = 0; j < iterations; j++)
                {
                    await ch.WriteAsync(1);
                }
            }, channel);
        }

        _ = Go(async () =>
        {
            await wg.WaitAsync();
            await channel.CompleteAsync();
        });

        var sum = await channel.SumAsync();

        if (sum != expectedSum)
        {
            throw new Exception("Sum is not correct");
        }
    }

    [Benchmark]
    public async Task Channel_WithTpl()
    {
        var channel = Channel.CreateUnbounded<int>();
        var writer = channel.Writer;
        var reader = channel.Reader;

        var producerTasks = new List<Task>();

        for (var i = 0; i < concurrency; i++)
        {
            producerTasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < iterations; j++)
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

        var sum = 0;

        await foreach (var value in reader.ReadAllAsync())
        {
            sum += value;
        }

        await completionTask;

        if (sum != expectedSum)
        {
            throw new Exception("Sum is not correct");
        }
    }
}

class Program
{
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<GoBenchmark>();
    }
}