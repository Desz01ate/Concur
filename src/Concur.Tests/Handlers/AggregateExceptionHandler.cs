namespace Concur.Tests.Handlers;

using Abstractions;

/// <summary>
/// An example for exception aggregation.
/// </summary>
internal sealed class AggregateExceptionHandler : IExceptionHandler
{
    private readonly List<Exception> exceptions = [];
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async ValueTask HandleAsync(IExceptionContext context)
    {
        await this.semaphore.WaitAsync();

        try
        {

            this.exceptions.Add(context.Exception);
        }
        finally
        {
            this.semaphore.Release();
        }
    }

    public AggregateException GetAggregateException()
    {
        return new AggregateException(this.exceptions);
    }
}