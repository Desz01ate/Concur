namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

public sealed class ConcurContextModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var context = bindingContext.HttpContext.RequestServices.GetRequiredService<Context>();
        bindingContext.Result = ModelBindingResult.Success(context);
        return Task.CompletedTask;
    }
}
