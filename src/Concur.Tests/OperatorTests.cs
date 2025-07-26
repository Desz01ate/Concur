namespace Concur.Tests;

using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Concur.Implementations;
using Xunit;

public class OperatorTests
{
    [Fact]
    public void WriteOperator_WritesValueToChannel()
    {
        // Arrange
        var channel = new DefaultChannel<int>();
        const int expectedValue = 42;

        // Act
        _ = channel << expectedValue;

        // Assert
        var actualValue = -channel;
        Assert.Equal(expectedValue, actualValue);
    }

    [Fact]
    public void ReadOperator_ReadsValueFromChannel()
    {
        // Arrange
        var channel = new DefaultChannel<string>();
        const string expectedValue = "test message";
        _ = channel << expectedValue;

        // Act
        var actualValue = -channel;

        // Assert
        Assert.Equal(expectedValue, actualValue);
    }

    [Fact]
    public async Task ReadOperator_BlocksUntilValueAvailable()
    {
        // Arrange
        var channel = new DefaultChannel<int>();
        const int expectedValue = 100;
        var valueRead = false;

        // Start a task that will read from the channel
        var readTask = Task.Run(() =>
        {
            var value = -channel;
            Assert.Equal(expectedValue, value);
            valueRead = true;
            return value;
        });

        // Give the read task time to start and block
        await Task.Delay(100);

        // Verify that the read task is blocked
        Assert.False(valueRead);

        // Act - write a value to unblock the read
        _ = channel << expectedValue;

        // Wait for the read task to complete
        await Task.WhenAny(readTask, Task.Delay(1000));

        // Assert that the value was read successfully
        Assert.True(valueRead);
        Assert.Equal(expectedValue, await readTask);
    }

    [Fact]
    public void WriteOperator_CanChainMultipleWrites()
    {
        // Arrange
        var channel = new DefaultChannel<int>();

        // Act - chain multiple writes using the operator
        _ = channel << 1 << 2 << 3;

        // Assert - read the values in order
        Assert.Equal(1, -channel);
        Assert.Equal(2, -channel);
        Assert.Equal(3, -channel);
    }

    [Fact]
    public async Task WriteOperator_WorksWithBoundedChannel()
    {
        // Arrange - create a bounded channel with capacity 2
        var channel = new DefaultChannel<int>(2);

        // Act - fill the channel to capacity
        _ = channel << 1 << 2;

        // Start a task that will try to write one more item
        var writeTask = Task.Run(() =>
        {
            // This should block until space is available
            _ = channel << 3;
            return true;
        });

        // Give the write task time to start and block
        await Task.Delay(100);

        // The task should still be running (blocked)
        Assert.False(writeTask.IsCompleted);

        // Read an item to make space
        var value = -channel;
        Assert.Equal(1, value);

        // Wait for the write task to complete
        await Task.WhenAny(writeTask, Task.Delay(1000));

        // Assert that the write completed
        Assert.True(await writeTask);

        // Read the remaining values
        Assert.Equal(2, -channel);
        Assert.Equal(3, -channel);
    }

    [Fact]
    public void Operators_WorkWithDifferentDataTypes()
    {
        // Test with string
        var stringChannel = new DefaultChannel<string>();
        _ = stringChannel << "hello" << "world";
        Assert.Equal("hello", -stringChannel);
        Assert.Equal("world", -stringChannel);

        // Test with custom type
        var personChannel = new DefaultChannel<Person>();
        var person1 = new Person { Name = "John", Age = 30 };
        var person2 = new Person { Name = "Jane", Age = 25 };
        _ = personChannel << person1 << person2;
        var readPerson1 = -personChannel;
        var readPerson2 = -personChannel;

        Assert.Equal(person1.Name, readPerson1.Name);
        Assert.Equal(person1.Age, readPerson1.Age);
        Assert.Equal(person2.Name, readPerson2.Name);
        Assert.Equal(person2.Age, readPerson2.Age);
    }

    [Fact]
    public async Task Operators_WorkWithCompletedChannel()
    {
        // Arrange
        var channel = new DefaultChannel<int>();
        _ = channel << 1 << 2;
        await channel.CompleteAsync();

        // Act & Assert
        // We should be able to read existing items after completion
        Assert.Equal(1, -channel);
        Assert.Equal(2, -channel);

        // The next read should throw because the channel is completed and empty
        Assert.Throws<ChannelClosedException>(() => -channel);

        // Writing to a completed channel should throw
        var writeException = Assert.Throws<AggregateException>(() => channel << 3);
        Assert.Contains(writeException.InnerExceptions, ex => ex.GetType().Name.Contains("Channel"));
    }

    private class Person
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }
}