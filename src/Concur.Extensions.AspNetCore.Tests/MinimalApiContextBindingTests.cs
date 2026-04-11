namespace Concur.Extensions.AspNetCore.Tests;

using Concur;
using Concur.Extensions.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

public class MinimalApiContextBindingTests
{
    [Fact]
    public async Task RequestDelegateFactory_ResolvesContextFromRequestServices()
    {
        var services = new ServiceCollection();
        services.AddConcurContext(options =>
        {
            options.AddHttpContextToken(http => http.RequestAborted);
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var requestCts = new CancellationTokenSource();

        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = requestCts.Token,
        };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        var expected = scope.ServiceProvider.GetRequiredService<Context>();
        Context? captured = null;

        var result = RequestDelegateFactory.Create(([FromServices] Context context) =>
        {
            captured = context;
        });
        Assert.NotNull(result.RequestDelegate);

        await result.RequestDelegate!(http);

        Assert.Same(expected, captured);
    }
}
