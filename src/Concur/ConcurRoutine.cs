namespace Concur;

using System;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading.Tasks;

/// <summary>
/// Provides static methods for running concurrent operations, inspired by Golang's goroutines.
/// </summary>
public static class ConcurRoutine
{
    /// <summary>
    /// Gets or sets the action to execute when an exception occurs in a background task.
    /// For production environments, this should be configured to use a proper logging framework.
    /// </summary>
    /// <example>
    /// Configure with a logging framework:
    /// <code>
    /// // Using Microsoft.Extensions.Logging
    /// ConcurRoutine.OnException = exception => logger.LogError(exception, "Error in background task");
    /// 
    /// // Or with Serilog
    /// ConcurRoutine.OnException = exception => Log.Error(exception, "Error in background task");
    /// </code>
    /// </example>
    public static Action<Exception> OnException { get; set; } = static e =>
    {
        // Default no-op handler - this should be configured by the application
#if DEBUG
        Console.WriteLine($"[ConcurRoutine] Exception in background task: {e}");
        Debug.WriteLine($"[ConcurRoutine] Exception in background task: {e}");
#endif
    };

    /// <summary>
    /// Runs a fire-and-forget synchronous action on a background thread.
    /// Any exceptions are caught and written to the console.
    /// </summary>
    /// <param name="action">The synchronous action to execute.</param>
    public static void Go(Action action)
    {
        // Using Task.Run to execute the action on a ThreadPool thread.
        _ = Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                OnException(e);
            }
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// Any exceptions are caught and passed to OnException.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static Task Go(Func<Task> func)
    {
        return Task.Run(async () =>
        {
            try
            {
                await func();
            }
            catch (Exception e)
            {
                OnException(e);
            }
        });
    }

    /// <summary>
    /// Runs a producer function that writes to a given channel.
    /// The producer is responsible for completing the channel when it's done.
    /// </summary>
    /// <param name="func">The producer function that receives the channel writer to write to.</param>
    /// <param name="channel">The writer for the channel that the func will write to.</param>
    /// <typeparam name="T">The type of data in the channel.</typeparam>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static Task Go<T>(Func<ChannelWriter<T>, Task> func, ChannelWriter<T> channel)
    {
        return Task.Run(async () =>
        {
            try
            {
                await func(channel);
            }
            catch (Exception e)
            {
                channel.TryComplete(e);
            }
        });
    }

    /// <summary>
    /// Creates a channel, runs a producer goroutine that writes to it, and returns the reader.
    /// The producer is responsible for completing the channel when it's done.
    /// </summary>
    /// <param name="producer">The function that produces values and writes them to the channel.</param>
    /// <param name="capacity">Optional capacity for a bounded channel. If null, an unbounded channel is created.</param>
    /// <typeparam name="T">The type of data in the channel.</typeparam>
    /// <returns>A ChannelReader that a consumer can read from.</returns>
    public static ChannelReader<T> Go<T>(Func<ChannelWriter<T>, Task> producer, int? capacity = null)
    {
        var channel = capacity.HasValue
            ? Channel.CreateBounded<T>(new BoundedChannelOptions(capacity.Value)
            {
                FullMode = BoundedChannelFullMode.Wait, // Mimics Go channel behavior
            })
            : Channel.CreateUnbounded<T>();

        _ = Task.Run(async () =>
        {
            try
            {
                await producer(channel.Writer);
            }
            catch (Exception e)
            {
                channel.Writer.TryComplete(e);
            }
        });

        return channel.Reader;
    }
}