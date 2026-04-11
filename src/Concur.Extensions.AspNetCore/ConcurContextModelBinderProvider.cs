namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Mvc.ModelBinding;

public sealed class ConcurContextModelBinderProvider : IModelBinderProvider
{
    private static readonly IModelBinder Binder = new ConcurContextModelBinder();

    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Metadata.ModelType == typeof(Context)
            ? Binder
            : null;
    }
}
