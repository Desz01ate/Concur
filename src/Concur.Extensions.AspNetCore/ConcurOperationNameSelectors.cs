namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Provides common operation-name selector functions for request-scoped <see cref="Context"/> creation.
/// </summary>
public static class ConcurOperationNameSelectors
{
    /// <summary>
    /// Selects an operation name from the current endpoint metadata with fallback behavior.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <returns>
    /// The route pattern when available; otherwise endpoint display name; otherwise <c>http-request</c>.
    /// </returns>
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
