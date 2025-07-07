namespace Concur.Tests;

using Abstractions;
using Implementations;
using static ConcurRoutine;

public class WaitGroupTests
{
    [Fact]
    public async Task Go_WithWaitGroup_WithAction_ExecutesAction()
    {
        // Arrange
        var wg = new WaitGroup();
        bool[] executeResults = [false, false, false];

        // Act
        Go(wg, () =>
        {
            Thread.Sleep(100);
            executeResults[0] = true;
        });
        Go(wg, () =>
        {
            Thread.Sleep(500);
            executeResults[1] = true;
        });
        Go(wg, () =>
        {
            Thread.Sleep(1000);
            executeResults[2] = true;
        });

        await wg.WaitAsync();

        // Assert
        Assert.All(executeResults, Assert.True);
    }

    [Fact]
    public async Task Go_WithWaitGroup_WithTaskFunc_ExecutesTask()
    {
        // Arrange
        var wg = new WaitGroup();
        bool[] executeResults = [false, false, false];

        // Act
        _ = Go(wg, async () =>
        {
            await Task.Delay(100);
            executeResults[0] = true;
        });
        _ = Go(wg, async () =>
        {
            await Task.Delay(500);
            executeResults[1] = true;
        });
        _ = Go(wg, async () =>
        {
            await Task.Delay(1000);
            executeResults[2] = true;
        });

        await wg.WaitAsync();

        // Assert
        Assert.All(executeResults, Assert.True);
    }

    [Fact]
    public async Task Go_WithWaitGroup_WithChannelFunc_WritesToChannel()
    {
        // Arrange
        var wg = new WaitGroup();
        var channel = new DefaultChannel<int>();
        var values = new[] { 1, 2, 3, 4, 5 };
        Func<IChannel<int>, Task> producer = async chan =>
        {
            foreach (var value in values)
            {
                await chan.WriteAsync(value);
                await Task.Delay(10);
            }
        };

        // Act
        _ = Go(wg, producer, channel);
        _ = Go(wg, producer, channel);
        _ = Go(wg, producer, channel);

        await wg.WaitAsync();
        await channel.CompleteAsync();

        // Assert
        int[] expectedResult = [..values, ..values, ..values];
        var collected = new List<int>();

        await foreach (var item in channel)
        {
            collected.Add(item);

            if (collected.Count >= expectedResult.Length)
            {
                break;
            }
        }

        Assert.Equivalent(expectedResult, collected);
    }
}