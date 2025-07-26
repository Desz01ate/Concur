namespace Concur;

using System;
using System.Threading.Tasks;
using Abstractions;
using Contexts;
using Handlers;
using Implementations;

/// <summary>
/// Provides static methods for running concurrent operations, inspired by Golang's goroutines.
/// </summary>
public static class ConcurRoutine
{
    /// <summary>
    /// The default exception handler used when no specific handler is provided.
    /// </summary>
    private static IExceptionHandler DefaultExceptionHandler { get; } =
        new DefaultLoggingExceptionHandler();

    /// <summary>
    /// Internal method to handle exceptions within Go routines.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="routineId">The unique identifier for the Go routine.</param>
    /// <param name="options">The Go options containing handler and metadata.</param>
    private static async ValueTask HandleExceptionAsync(Exception exception, string routineId, GoOptions? options)
    {
        var handler = options?.ExceptionHandler ?? DefaultExceptionHandler;
        var context = new ExceptionContext(
            exception,
            routineId,
            options?.OperationName,
            options?.Metadata);

        try
        {
            await handler.HandleAsync(context);
        }
        catch
        {
            // If the exception handler itself throws, we can't do much about it
            // In this case, we silently ignore to prevent infinite loops
        }
    }

    /// <summary>
    /// Generates a unique routine ID for tracking purposes.
    /// </summary>
    /// <returns>A unique string identifier.</returns>
    private static string GenerateRoutineId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Executes an action with concurrency limiting if specified in options.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="options">Optional configuration options including concurrency limits.</param>
    private static async Task ExecuteWithConcurrencyLimitAsync(Func<Task> action, GoOptions? options)
    {
        var semaphore = ConcurrencyManager.GetSemaphore(options);

        if (semaphore == null)
        {
            await action();
            return;
        }

        await semaphore.WaitAsync();

        try
        {
            await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Runs a fire-and-forget synchronous action on a background thread.
    /// </summary>
    /// <param name="action">The synchronous action to execute.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go(Action action, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go(Func<Task> func, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func();
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Creates a channel, runs a producer goroutine that writes to it, and returns the reader.
    /// The producer is responsible for completing the channel when it's done.
    /// </summary>
    /// <param name="producer">The function that produces values and writes them to the channel.</param>
    /// <param name="capacity">Optional capacity for a bounded channel. If null, an unbounded channel is created.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <typeparam name="T">The type of data in the channel.</typeparam>
    /// <returns>A IConcurChannel that a consumer can read from.</returns>
    public static IChannel<T, DefaultChannel<T>> Go<T>(Func<DefaultChannel<T>, Task> producer, int? capacity = null, GoOptions? options = null)
    {
        var channel = new DefaultChannel<T>(capacity);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await producer(channel);
                }
                catch (Exception e)
                {
                    await channel.FailAsync(e);
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
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
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <remarks>
    /// This overload is ideal for fire-and-forget synchronous operations when you need to know when a group of them has finished.
    /// A <c>finally</c> block ensures that <c>wg.Done()</c> is called, decrementing the counter, regardless of whether the action
    /// completed successfully or threw an exception.
    /// </remarks>
    public static void Go(WaitGroup wg, Action action, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go(WaitGroup wg, Func<Task> func, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func();
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    #endregion

    #region Generics - Sync

    /// <summary>
    /// Runs a synchronous function on a background thread.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="p">The parameter to pass to the function.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T>(Action<T> func, T p, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2>(Action<T1, T2> func, T1 p1, T2 p2, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3>(Action<T1, T2, T3> func, T1 p1, T2 p2, T3 p3, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4>(Action<T1, T2, T3, T4> func, T1 p1, T2 p2, T3 p3, T4 p4, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4, T5>(Action<T1, T2, T3, T4, T5> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4, p5);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4, T5, T6>(Action<T1, T2, T3, T4, T5, T6> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4, p5, p6);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="p7">The seventh parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4, T5, T6, T7>(Action<T1, T2, T3, T4, T5, T6, T7> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4, p5, p6, p7);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="p7">The seventh parameter.</param>
    /// <param name="p8">The eighth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4, T5, T6, T7, T8>(Action<T1, T2, T3, T4, T5, T6, T7, T8> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7, T8 p8, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4, p5, p6, p7, p8);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    #endregion

    #region Generics - Async

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="p">The parameter to pass to the function.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go<T>(Func<T, Task> func, T p, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go<T1, T2>(Func<T1, T2, Task> func, T1 p1, T2 p2, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go<T1, T2, T3>(Func<T1, T2, T3, Task> func, T1 p1, T2 p2, T3 p3, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go<T1, T2, T3, T4>(Func<T1, T2, T3, T4, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go<T1, T2, T3, T4, T5>(Func<T1, T2, T3, T4, T5, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4, p5);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go<T1, T2, T3, T4, T5, T6>(Func<T1, T2, T3, T4, T5, T6, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4, p5, p6);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="p7">The seventh parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go<T1, T2, T3, T4, T5, T6, T7>(Func<T1, T2, T3, T4, T5, T6, T7, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4, p5, p6, p7);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread.
    /// </summary>
    /// <param name="func">The async function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="p7">The seventh parameter.</param>
    /// <param name="p8">The eighth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static void Go<T1, T2, T3, T4, T5, T6, T7, T8>(Func<T1, T2, T3, T4, T5, T6, T7, T8, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7, T8 p8, GoOptions? options = null)
    {
        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4, p5, p6, p7, p8);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
            }, options);
        });
    }

    #endregion

    #region Generics - Sync, WaitGroup

    /// <summary>
    /// Runs a synchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The function to execute.</param>
    /// <param name="p">The parameter to pass to the function.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T>(WaitGroup wg, Action<T> func, T p, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2>(WaitGroup wg, Action<T1, T2> func, T1 p1, T2 p2, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3>(WaitGroup wg, Action<T1, T2, T3> func, T1 p1, T2 p2, T3 p3, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4>(WaitGroup wg, Action<T1, T2, T3, T4> func, T1 p1, T2 p2, T3 p3, T4 p4, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4, T5>(WaitGroup wg, Action<T1, T2, T3, T4, T5> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4, p5);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4, T5, T6>(WaitGroup wg, Action<T1, T2, T3, T4, T5, T6> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4, p5, p6);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="p7">The seventh parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4, T5, T6, T7>(WaitGroup wg, Action<T1, T2, T3, T4, T5, T6, T7> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4, p5, p6, p7);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs a synchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="p7">The seventh parameter.</param>
    /// <param name="p8">The eighth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    public static void Go<T1, T2, T3, T4, T5, T6, T7, T8>(WaitGroup wg, Action<T1, T2, T3, T4, T5, T6, T7, T8> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7, T8 p8, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    func(p1, p2, p3, p4, p5, p6, p7, p8);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    #endregion

    #region Generics - Async, WaitGroup

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="p">The parameter to pass to the function.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go<T>(WaitGroup wg, Func<T, Task> func, T p, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go<T1, T2>(WaitGroup wg, Func<T1, T2, Task> func, T1 p1, T2 p2, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go<T1, T2, T3>(WaitGroup wg, Func<T1, T2, T3, Task> func, T1 p1, T2 p2, T3 p3, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go<T1, T2, T3, T4>(WaitGroup wg, Func<T1, T2, T3, T4, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go<T1, T2, T3, T4, T5>(WaitGroup wg, Func<T1, T2, T3, T4, T5, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4, p5);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go<T1, T2, T3, T4, T5, T6>(WaitGroup wg, Func<T1, T2, T3, T4, T5, T6, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4, p5, p6);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="p7">The seventh parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go<T1, T2, T3, T4, T5, T6, T7>(WaitGroup wg, Func<T1, T2, T3, T4, T5, T6, T7, Task> func, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7, GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4, p5, p6, p7);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    /// <summary>
    /// Runs an asynchronous function on a background thread and associates it with a <see cref="WaitGroup"/>.
    /// This method automatically handles the WaitGroup counter.
    /// </summary>
    /// <param name="wg">The <see cref="WaitGroup"/> instance to associate this task with.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <param name="p1">The first parameter.</param>
    /// <param name="p2">The second parameter.</param>
    /// <param name="p3">The third parameter.</param>
    /// <param name="p4">The fourth parameter.</param>
    /// <param name="p5">The fifth parameter.</param>
    /// <param name="p6">The sixth parameter.</param>
    /// <param name="p7">The seventh parameter.</param>
    /// <param name="p8">The eighth parameter.</param>
    /// <param name="options">Optional configuration options for the Go routine.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Use this overload for asynchronous operations that return a <see cref="Task"/>. It allows you to group several async
    /// operations and wait for their collective completion. The <c>finally</c> block guarantees that <c>wg.Done()</c>
    /// is called after the <c>await func()</c> completes or throws an exception.
    /// </remarks>
    public static void Go<T1, T2, T3, T4, T5, T6, T7, T8>(
        WaitGroup wg,
        Func<T1, T2, T3, T4, T5, T6, T7, T8, Task> func,
        T1 p1,
        T2 p2,
        T3 p3,
        T4 p4,
        T5 p5,
        T6 p6,
        T7 p7,
        T8 p8,
        GoOptions? options = null)
    {
        wg.Add(1);

        _ = Task.Run(async () =>
        {
            await ExecuteWithConcurrencyLimitAsync(async () =>
            {
                try
                {
                    await func(p1, p2, p3, p4, p5, p6, p7, p8);
                }
                catch (Exception e)
                {
                    await HandleExceptionAsync(e, GenerateRoutineId(), options);
                }
                finally
                {
                    wg.Done();
                }
            }, options);
        });
    }

    #endregion
}