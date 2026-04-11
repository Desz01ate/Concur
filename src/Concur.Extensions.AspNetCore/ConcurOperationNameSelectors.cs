namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class ConcurOperationNameSelectors
{
    public static string? Default(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (http.GetEndpoint() is RouteEndpoint routeEndpoint &&
            !string.IsNullOrWhiteSpace(routeEndpoint.RoutePattern.RawText))
        {
            return routeEndpoint.RoutePattern.RawText;
        }

        var displayName = http.GetEndpoint()?.DisplayName;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        return "http-request";
    }
}
