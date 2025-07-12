namespace Concur;

using Abstractions;

/// <summary>
/// Configuration options for Go routines.
/// </summary>
public sealed class GoOptions
{
    /// <summary>
    /// The exception handler to use for this operation. If null, uses the default handler.
    /// </summary>
    public IExceptionHandler? ExceptionHandler { get; init; }

    /// <summary>
    /// Optional operation name for debugging and logging purposes.
    /// </summary>
    public string? OperationName { get; init; }

    /// <summary>
    /// Additional metadata to include with the exception context.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = 
        new Dictionary<string, object?>();

    /// <summary>
    /// Gets the default options instance.
    /// </summary>
    public static GoOptions Default { get; } = new();
}