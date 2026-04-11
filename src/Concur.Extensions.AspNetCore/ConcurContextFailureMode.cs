namespace Concur.Extensions.AspNetCore;

/// <summary>
/// Defines how context creation handles token-source evaluation failures.
/// </summary>
public enum ConcurContextFailureMode
{
    /// <summary>
    /// Fail context creation when any configured token source throws.
    /// </summary>
    Strict,

    /// <summary>
    /// Ignore failing token sources and continue context creation with remaining sources.
    /// </summary>
    Lenient,
}
