using static Concur.ConcurRoutine;

namespace Concur.Tests;

public class ConcurrencyLimitingTests
{
    [Fact]
    public async Task Go_WithMaxConcurrency_LimitsParallelExecution()
    {
        var concurrentCount = 0;
        var maxConcurrentCount = 0;
        const int totalTasks = 10;
        const int maxConcurrency = 3;

        var options = new GoOptions { MaxConcurrency = maxConcurrency };
        var wg = new WaitGroup();

        for (var i = 0; i < totalTasks; i++)
        {
            Go(wg, async () =>
            {
                var current = Interlocked.Increment(ref concurrentCount);

                while (true)
                {
                    var currentMax = maxConcurrentCount;
                    if (current <= currentMax || Interlocked.CompareExchange(ref maxConcurrentCount, current, currentMax) == currentMax)
                        break;
                }

                await Task.Delay(100);
                Interlocked.Decrement(ref concurrentCount);
            }, options);
        }

        await wg.WaitAsync();

        Assert.True(maxConcurrentCount <= maxConcurrency, 
            $"Max concurrent count {maxConcurrentCount} exceeded limit {maxConcurrency}");
        Assert.True(maxConcurrentCount > 0, "No concurrent execution detected");
    }

    [Fact]
    public async Task Go_WithCustomSemaphore_UsesProvidedSemaphore()
    {
        var customSemaphore = new SemaphoreSlim(2, 2);
        var concurrentCount = 0;
        var maxConcurrentCount = 0;
        const int totalTasks = 6;

        var options = new GoOptions { ConcurrencyLimiter = customSemaphore };
        var wg = new WaitGroup();

        for (var i = 0; i < totalTasks; i++)
        {
            Go(wg, async () =>
            {
                var current = Interlocked.Increment(ref concurrentCount);

                while (true)
                {
                    var currentMax = maxConcurrentCount;
                    if (current <= currentMax || Interlocked.CompareExchange(ref maxConcurrentCount, current, currentMax) == currentMax)
                        break;
                }

                await Task.Delay(100);
                Interlocked.Decrement(ref concurrentCount);
            }, options);
        }

        await wg.WaitAsync();

        Assert.True(maxConcurrentCount <= 2, 
            $"Max concurrent count {maxConcurrentCount} exceeded semaphore limit 2");
        Assert.True(maxConcurrentCount > 0, "No concurrent execution detected");
    }

    [Fact]
    public async Task Go_WithoutConcurrencyLimits_RunsUnlimited()
    {
        var concurrentCount = 0;
        var maxConcurrentCount = 0;
        const int totalTasks = 20;

        var wg = new WaitGroup();

        for (var i = 0; i < totalTasks; i++)
        {
            Go(wg, async () =>
            {
                var current = Interlocked.Increment(ref concurrentCount);

                while (true)
                {
                    var currentMax = maxConcurrentCount;
                    if (current <= currentMax || Interlocked.CompareExchange(ref maxConcurrentCount, current, currentMax) == currentMax)
                        break;
                }

                await Task.Delay(50);
                Interlocked.Decrement(ref concurrentCount);
            });
        }

        await wg.WaitAsync();

        Assert.True(maxConcurrentCount > 5, 
            $"Expected high concurrency without limits, but got {maxConcurrentCount}");
    }

    [Fact]
    public async Task Go_AsyncWithMaxConcurrency_LimitsParallelExecution()
    {
        var concurrentCount = 0;
        var maxConcurrentCount = 0;
        const int totalTasks = 8;
        const int maxConcurrency = 2;

        var options = new GoOptions { MaxConcurrency = maxConcurrency };
        var wg = new WaitGroup();

        for (var i = 0; i < totalTasks; i++)
        {
            Go(wg, async () =>
            {
                var current = Interlocked.Increment(ref concurrentCount);

                while (true)
                {
                    var currentMax = maxConcurrentCount;
                    if (current <= currentMax || Interlocked.CompareExchange(ref maxConcurrentCount, current, currentMax) == currentMax)
                        break;
                }

                await Task.Delay(100);
                Interlocked.Decrement(ref concurrentCount);
            }, options);
        }

        await wg.WaitAsync();

        Assert.True(maxConcurrentCount <= maxConcurrency, 
            $"Max concurrent count {maxConcurrentCount} exceeded limit {maxConcurrency}");
        Assert.True(maxConcurrentCount > 0, "No concurrent execution detected");
    }

    [Fact]
    public async Task Go_ParameterizedWithConcurrency_LimitsExecution()
    {
        var concurrentCount = 0;
        var maxConcurrentCount = 0;
        const int totalTasks = 6;
        const int maxConcurrency = 2;

        var options = new GoOptions { MaxConcurrency = maxConcurrency };
        var wg = new WaitGroup();

        for (var i = 0; i < totalTasks; i++)
        {
            Go(wg, async _ =>
            {
                var current = Interlocked.Increment(ref concurrentCount);

                while (true)
                {
                    var currentMax = maxConcurrentCount;
                    if (current <= currentMax || Interlocked.CompareExchange(ref maxConcurrentCount, current, currentMax) == currentMax)
                        break;
                }

                await Task.Delay(100);
                Interlocked.Decrement(ref concurrentCount);
            }, i, options);
        }

        await wg.WaitAsync();

        Assert.True(maxConcurrentCount <= maxConcurrency, 
            $"Max concurrent count {maxConcurrentCount} exceeded limit {maxConcurrency}");
    }

    [Fact]
    public async Task Go_BackwardCompatibility_WorksWithoutOptions()
    {
        var executionCount = 0;
        var wg = new WaitGroup();

        for (var i = 0; i < 5; i++)
        {
            Go(wg, () => Interlocked.Increment(ref executionCount));
        }

        await wg.WaitAsync();

        Assert.Equal(5, executionCount);
    }

    [Fact]
    public void ConcurrencyManager_GetSemaphore_PrioritizesCustomSemaphore()
    {
        var customSemaphore = new SemaphoreSlim(1, 1);
        var options = new GoOptions 
        { 
            ConcurrencyLimiter = customSemaphore,
            MaxConcurrency = 10,
        };

        var result = options.GetOrCreateSemaphore();

        Assert.Same(customSemaphore, result);
    }
}