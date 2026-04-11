namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Binds action parameters of type <see cref="Context"/> from request services.
/// </summary>
public sealed class ConcurContextModelBinder : IModelBinder
{
    /// <summary>
    /// Binds a <see cref="Context"/> instance for the current request.
    /// </summary>
    /// <param name="bindingContext">The current model-binding context.</param>
    /// <returns>A completed task.</returns>
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var context = bindingContext.HttpContext.RequestServices.GetRequiredService<Context>();
        bindingContext.Result = ModelBindingResult.Success(context);
        return Task.CompletedTask;
    }
}
