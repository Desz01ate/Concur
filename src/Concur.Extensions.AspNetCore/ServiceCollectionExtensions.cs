namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
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
            .Configure(options =>
            {
                if (options.ModelBinderProviders.Any(provider => provider is ConcurContextModelBinderProvider))
                {
                    return;
                }

                options.ModelBinderProviders.Insert(0, new ConcurContextModelBinderProvider());
            });

        services.AddHttpContextAccessor();
        services.AddScoped<IConcurContextFactory, DefaultConcurContextFactory>();
        services.AddScoped<Context>(sp =>
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
