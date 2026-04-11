namespace Concur.Extensions.AspNetCore;

using Microsoft.AspNetCore.Mvc.ModelBinding;

/// <summary>
/// Supplies <see cref="ConcurContextModelBinder"/> for parameters of type <see cref="Context"/>.
/// </summary>
public sealed class ConcurContextModelBinderProvider : IModelBinderProvider
{
    private readonly static IModelBinder Binder = new ConcurContextModelBinder();

    /// <summary>
    /// Returns a binder when the target model type is <see cref="Context"/>.
    /// </summary>
    /// <param name="context">The binder provider context.</param>
    /// <returns>The binder instance, or <see langword="null"/> when not applicable.</returns>
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Metadata.ModelType == typeof(Context)
            ? Binder
            : null;
    }
}
