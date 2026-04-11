namespace Concur.Extensions.AspNetCore.Tests;

using Concur;
using Concur.Extensions.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

public class ConcurContextModelBinderTests
{
    [Fact]
    public async Task BindModelAsync_ResolvesScopedContextFromRequestServices()
    {
        var services = new ServiceCollection();
        services.AddConcurContext(options =>
        {
            options.AddHttpContextToken(http => http.RequestAborted);
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        using var expected = scope.ServiceProvider.GetRequiredService<Context>();

        var metadataProvider = new EmptyModelMetadataProvider();
        var metadata = metadataProvider.GetMetadataForType(typeof(Context));
        var actionContext = new ActionContext(
            http,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var bindingContext = DefaultModelBindingContext.CreateBindingContext(
            actionContext,
            new CompositeValueProvider(),
            metadata,
            bindingInfo: null,
            modelName: nameof(Context));

        var binder = new ConcurContextModelBinder();
        await binder.BindModelAsync(bindingContext);

        Assert.True(bindingContext.Result.IsModelSet);
        Assert.Same(expected, Assert.IsType<Context>(bindingContext.Result.Model));
    }
}
