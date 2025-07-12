namespace Concur.Tests.Handlers;

using Abstractions;

internal sealed class TestChannelExceptionHandler : IExceptionHandler
{
    private readonly TestExceptionHandler innerHandler;
    private readonly ManualResetEventSlim resetEvent;

    public TestChannelExceptionHandler(TestExceptionHandler innerHandler, ManualResetEventSlim resetEvent)
    {
        this.innerHandler = innerHandler;
        this.resetEvent = resetEvent;
    }

    public async ValueTask HandleAsync(IExceptionContext context)
    {
        await this.innerHandler.HandleAsync(context);
        this.resetEvent.Set();
    }
}