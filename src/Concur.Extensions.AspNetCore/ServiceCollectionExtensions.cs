namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers ASP.NET Core integration services for request-scoped <see cref="Context"/> usage.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds request-scoped <see cref="Context"/> services and MVC binding support.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration callback.</param>
    /// <returns>The original <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddConcurContext(
        this IServiceCollection services,
        Action<ConcurContextOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ConcurContextOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<MvcOptions>()
                .Configure(
                    static options =>
                    {
                        if (options.ModelBinderProviders.Any(provider => provider is ConcurContextModelBinderProvider))
                        {
                            return;
                        }

                        options.ModelBinderProviders.Insert(0, new ConcurContextModelBinderProvider());
                    });

        services.AddHttpContextAccessor();
        services.AddScoped<IConcurContextFactory, DefaultConcurContextFactory>();
        services.AddScoped<Context>(static sp =>
        {
            var accessor = sp.GetRequiredService<IHttpContextAccessor>();
            var httpContext = accessor.HttpContext
                              ?? throw new InvalidOperationException("No active HttpContext for scoped Context resolution.");

            var factory = sp.GetRequiredService<IConcurContextFactory>();
            return factory.Create(httpContext);
        });

        return services;
    }
}