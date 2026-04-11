namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Http;

public interface IConcurContextFactory
{
    Context Create(HttpContext httpContext);
}
