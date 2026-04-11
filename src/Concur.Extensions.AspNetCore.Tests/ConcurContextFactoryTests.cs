namespace Concur.Extensions.AspNetCore.Tests;

using Concur;
using Concur.Extensions.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public class ConcurContextFactoryTests
{
    [Fact]
    public void Create_LinksHttpContextAndServiceTokens()
    {
        using var appStoppingCts = new CancellationTokenSource();
        var services = new ServiceCollection();
        services.AddSingleton(new FakeLifetime(appStoppingCts.Token));
        services.AddConcurContext(options =>
        {
            options.AddHttpContextToken(http => http.RequestAborted);
            options.AddServiceToken<FakeLifetime>(lifetime => lifetime.ApplicationStopping);
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var requestCts = new CancellationTokenSource();

        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = requestCts.Token,
        };
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "/users"));
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        using var context = scope.ServiceProvider.GetRequiredService<Context>();
        requestCts.Cancel();

        Assert.True(SpinWait.SpinUntil(() => context.IsCancellationRequested, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Create_CancelsWhenServiceTokenCancels()
    {
        using var appStoppingCts = new CancellationTokenSource();
        var services = new ServiceCollection();
        services.AddSingleton(new FakeLifetime(appStoppingCts.Token));
        services.AddConcurContext(options =>
        {
            options.AddServiceToken<FakeLifetime>(lifetime => lifetime.ApplicationStopping);
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        using var context = scope.ServiceProvider.GetRequiredService<Context>();
        appStoppingCts.Cancel();

        Assert.True(SpinWait.SpinUntil(() => context.IsCancellationRequested, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Create_FirstCancelWinsCause()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var context = Context.Background.WithCancel(new[] { first.Token, second.Token }, "test");

        first.Cancel();
        second.Cancel();

        var cause = Assert.IsType<OperationCanceledException>(context.CancellationCause);
        Assert.Equal(first.Token, cause.CancellationToken);
    }

    [Fact]
    public void StrictMode_ThrowsWhenSelectorFails()
    {
        var services = new ServiceCollection();
        services.AddConcurContext(options =>
        {
            options.FailureMode = ConcurContextFailureMode.Strict;
            options.AddHttpContextToken(_ => throw new InvalidOperationException("boom"));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<Context>());
    }

    [Fact]
    public void LenientMode_FallsBackWhenSelectorFails()
    {
        var services = new ServiceCollection();
        services.AddConcurContext(options =>
        {
            options.FailureMode = ConcurContextFailureMode.Lenient;
            options.AddHttpContextToken(_ => throw new InvalidOperationException("boom"));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        using var context = scope.ServiceProvider.GetRequiredService<Context>();
        Assert.False(context.IsCancellationRequested);
    }

    public sealed record FakeLifetime(CancellationToken ApplicationStopping);
}
