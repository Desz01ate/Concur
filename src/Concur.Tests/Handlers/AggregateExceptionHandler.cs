namespace Concur.Tests.Handlers;

using Abstractions;

/// <summary>
/// An example for exception aggregation.
/// </summary>
internal sealed class AggregateExceptionHandler : IExceptionHandler
{
    private readonly List<Exception> exceptions = [];

    public AggregateExceptionHandler()
    {
    }

    public ValueTask HandleAsync(IExceptionContext context)
    {
        this.exceptions.Add(context.Exception);

        return ValueTask.CompletedTask;
    }

    public AggregateException GetAggregateException()
    {
        return new AggregateException(this.exceptions);
    }
}