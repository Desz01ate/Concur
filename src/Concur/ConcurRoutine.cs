namespace Concur;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Abstractions;
using Implementations;

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
    public static Task Go<T>(Func<IChannel<T>, Task> func, IChannel<T> channel)
    {
        return Task.Run(async () =>
        {
            try
            {
                await func(channel);
            }
            catch (Exception e)
            {
                await channel.FailAsync(e);
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
    /// <returns>A IConcurChannel that a consumer can read from.</returns>
    public static IChannel<T> Go<T>(Func<IChannel<T>, Task> producer, int? capacity = null)
    {
        var channel = new DefaultChannel<T>(capacity);

        _ = Task.Run(async () =>
        {
            try
            {
                await producer(channel);
            }
            catch (Exception e)
            {
                await channel.FailAsync(e);
            }
        });

        return channel;
    }

    #region WaitGroup

    /// <summary>
    /// Runs a synchronous action on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles incrementing the WaitGroup counter before the action starts
    /// and decrementing it after the action completes.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="action">The synchronous action to execute.</param>
    /// <remarks>
    /// This overload is ideal for fire-and-forget synchronous operations when you need to know when a group of them has finished.
    /// A <c>finally</c> block ensures that <c>wg.Done()</c> is called, decrementing the counter, regardless of whether the action
    /// completed successfully or threw an exception.
    /// </remarks>
    public static void Go(WaitGroup wg, Action action)
    {
        wg.Add(1);

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
            finally
            {
                wg.Done();
            }
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static Task Go(WaitGroup wg, Func<Task> func)
    {
        wg.Add(1);

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
            finally
            {
                wg.Done();
            }
        });
    }

    /// <summary>
    /// Runs a producer-style asynchronous function that writes to a channel and associates the entire operation with a <see cref="WaitGroup"/>.
    /// </summary>
    /// <typeparam name="T">The type of data in the channel.</typeparam>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The producer function that receives an <see cref="IChannel{T}"/> to write to.</param>
    /// <param name="channel">The channel that the producer function will use.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// This is the most advanced WaitGroup overload, designed for scenarios where you have multiple producers writing
    /// to a shared channel and you need to wait for all producers to finish their work. The <c>finally</c> block ensures
    /// <c>wg.Done()</c> is called once the producer has finished its execution loop.
    /// </remarks>
    public static Task Go<T>(WaitGroup wg, Func<IChannel<T>, Task> func, IChannel<T> channel)
    {
        wg.Add(1);

        return Task.Run(async () =>
        {
            try
            {
                await func(channel);
            }
            catch (Exception e)
            {
                await channel.FailAsync(e);
            }
            finally
            {
                wg.Done();
            }
        });
    }

    #endregion
}