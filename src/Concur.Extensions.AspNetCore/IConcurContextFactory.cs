namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Creates request-scoped <see cref="Context"/> instances from an ASP.NET Core <see cref="HttpContext"/>.
/// </summary>
public interface IConcurContextFactory
{
    /// <summary>
    /// Creates a <see cref="Context"/> for the provided request.
    /// </summary>
    /// <param name="httpContext">The current request context.</param>
    /// <returns>The request-scoped <see cref="Context"/>.</returns>
    Context Create(HttpContext httpContext);
}
