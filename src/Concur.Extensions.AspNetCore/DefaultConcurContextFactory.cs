namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

internal sealed class DefaultConcurContextFactory : IConcurContextFactory
{
    private readonly ConcurContextOptions options;

    public DefaultConcurContextFactory(IOptions<ConcurContextOptions> options)
    {
        this.options = options.Value;
    }

    public Context Create(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var tokens = new List<CancellationToken>();

        foreach (var tokenSource in this.options.TokenSources)
        {
            try
            {
                var token = tokenSource(httpContext, httpContext.RequestServices);
                if (token.CanBeCanceled)
                {
                    tokens.Add(token);
                }
            }
            catch when (this.options.FailureMode == ConcurContextFailureMode.Lenient)
            {
                // In lenient mode, skip failing token sources.
            }
        }

        var operationName = this.options.OperationNameSelector(httpContext);
        return tokens.Count == 0
            ? Context.Background.WithCancel(operationName)
            : Context.Background.WithCancel(tokens, operationName);
    }
}
