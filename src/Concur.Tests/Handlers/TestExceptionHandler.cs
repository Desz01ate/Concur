namespace Concur.Tests.Handlers;

using System.Collections.Concurrent;
using Concur.Abstractions;

public class TestExceptionHandler : IExceptionHandler
{
    private readonly ConcurrentQueue<IExceptionContext> capturedExceptions = new();

    public ValueTask HandleAsync(IExceptionContext context)
    {
        this.capturedExceptions.Enqueue(context);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<IExceptionContext> GetCapturedExceptions()
    {
        return this.capturedExceptions.ToArray();
    }

    public void Clear()
    {
        while (this.capturedExceptions.TryDequeue(out _))
        {
            // Clear all exceptions
        }
    }
}