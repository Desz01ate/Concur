namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public sealed class ConcurContextOptions
{
    private readonly List<Func<HttpContext, IServiceProvider, CancellationToken>> tokenSources = new();

    public ConcurContextFailureMode FailureMode { get; set; } = ConcurContextFailureMode.Strict;

    public Func<HttpContext, string?> OperationNameSelector { get; set; } = ConcurOperationNameSelectors.Default;

    internal IReadOnlyList<Func<HttpContext, IServiceProvider, CancellationToken>> TokenSources => this.tokenSources;

    public void AddHttpContextToken(Func<HttpContext, CancellationToken> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        this.tokenSources.Add((http, _) => selector(http));
    }

    public void AddServiceToken<TService>(Func<TService, CancellationToken> selector)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(selector);
        this.tokenSources.Add((_, services) => selector(services.GetRequiredService<TService>()));
    }
}
