namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Configures request-scoped <see cref="Context"/> creation for ASP.NET Core integration.
/// </summary>
public sealed class ConcurContextOptions
{
    private readonly List<Func<HttpContext, IServiceProvider, CancellationToken>> tokenSources = [];

    /// <summary>
    /// Gets or sets how failures from configured token sources are handled.
    /// </summary>
    public ConcurContextFailureMode FailureMode { get; set; } = ConcurContextFailureMode.Strict;

    /// <summary>
    /// Gets or sets the selector used to derive an operation name for each request context.
    /// </summary>
    public Func<HttpContext, string?> OperationNameSelector { get; set; } = ConcurOperationNameSelectors.Default;

    internal IReadOnlyList<Func<HttpContext, IServiceProvider, CancellationToken>> TokenSources => this.tokenSources;

    /// <summary>
    /// Adds a request token source resolved directly from the current <see cref="HttpContext"/>.
    /// </summary>
    /// <param name="selector">The token selector.</param>
    public void AddHttpContextToken(Func<HttpContext, CancellationToken> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        this.tokenSources.Add((http, _) => selector(http));
    }

    /// <summary>
    /// Adds a request token source resolved from a service in the current request scope.
    /// </summary>
    /// <typeparam name="TService">The service type used to resolve the token.</typeparam>
    /// <param name="selector">The token selector.</param>
    public void AddServiceToken<TService>(Func<TService, CancellationToken> selector)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(selector);
        this.tokenSources.Add((_, services) => selector(services.GetRequiredService<TService>()));
    }
}
