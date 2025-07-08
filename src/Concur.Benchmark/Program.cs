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
[WarmupCount(100)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class GoBenchmark
{
    private const int Iterations = 1_000_000;
    private const int Concurrency = 16;
    private const int ExpectedSum = Iterations * Concurrency;

    [Benchmark]
    public async Task Goroutine_WithWaitGroup()
    {
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();

        for (var i = 0; i < Concurrency; i++)
        {
            _ = Go(wg, async ch =>
            {
                for (var j = 0; j < Iterations; j++)
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

        if (sum != ExpectedSum)
        {
            throw new Exception("Sum is not correct");
        }
    }

    [Benchmark(Baseline = true)]
    public async Task Channel_WithTpl()
    {
        var channel = Channel.CreateUnbounded<int>();
        var writer = channel.Writer;
        var reader = channel.Reader;

        var producerTasks = new List<Task>();

        for (var i = 0; i < Concurrency; i++)
        {
            producerTasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < Iterations; j++)
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

        if (sum != ExpectedSum)
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