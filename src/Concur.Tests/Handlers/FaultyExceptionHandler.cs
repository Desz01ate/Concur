namespace Concur.Tests.Handlers;

using Abstractions;

internal sealed class FaultyExceptionHandler : IExceptionHandler
{
    public ValueTask HandleAsync(IExceptionContext context)
    {
        throw new InvalidOperationException("Exception in handler");
    }
}