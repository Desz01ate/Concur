namespace Concur.Abstractions;

/// <summary>
/// Defines a concurrent channel for asynchronous data flow, allowing both writing and reading of items.
/// This interface combines a contract for producers to write items with the ability for consumers
/// to asynchronously enumerate the items in the channel via IAsyncEnumerable.
/// </summary>
/// <typeparam name="T">The type of data that can be written to and read from the channel.</typeparam>
/// <typeparam name="TSelf">The concrete type that implements this interface.</typeparam>
public interface IChannel<T, TSelf> : IAsyncEnumerable<T>
    where TSelf : IChannel<T, TSelf>
{
    /// <summary>
    /// Writes an item to the channel asynchronously.
    /// </summary>
    /// <param name="item">The item to be written to the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> that represents the asynchronous write operation.</returns>
    ValueTask WriteAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the channel as complete, indicating that no more items will be written.
    /// This will signal the end of the asynchronous enumeration for consumers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> that represents the asynchronous completion operation.</returns>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the channel as faulted, propagating an exception to consumers.
    /// This indicates that the channel has terminated due to an error, which will be thrown
    /// during asynchronous enumeration.
    /// </summary>
    /// <param name="ex">The exception that caused the channel to fail.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> that represents the asynchronous fault operation.</returns>
    ValueTask FailAsync(Exception ex, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Defines a contract for a write operator.
    /// </summary>
    static abstract TSelf operator <<(TSelf channel, T item);

    /// <summary>
    /// Defines a contract for a read-and-return operator.
    /// </summary>
    static abstract T operator -(TSelf channel);
}