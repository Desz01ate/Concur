namespace Concur.Implementations;

using System.Threading.Channels;
using Abstractions;

/// <summary>
/// Provides a default implementation of the <see cref="IConcurChannel{T}"/> interface.
/// This class uses the <see cref="System.Threading.Channels.Channel{T}"/> for its underlying implementation,
/// supporting both bounded and unbounded channel behaviors.
/// </summary>
/// <typeparam name="T">The type of data handled by the channel.</typeparam>
public sealed class DefaultChannel<T> : IChannel<T>
{
    private readonly Channel<T> channel;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultChannel{T}"/> class.
    /// </summary>
    /// <param name="capacity">
    /// The bounded capacity of the channel. If null, an unbounded channel is created.
    /// If a value is provided, the channel will block writes when the capacity is reached
    /// until space becomes available.
    /// </param>
    public DefaultChannel(int? capacity = null)
    {
        // Create a bounded channel if capacity is specified, otherwise create an unbounded one.
        this.channel =
            capacity.HasValue
                ? Channel.CreateBounded<T>(new BoundedChannelOptions(capacity.Value)
                {
                    // This setting ensures that when the channel is full, write operations will wait
                    // for space to become available, mimicking the behavior of Go channels.
                    FullMode = BoundedChannelFullMode.Wait,
                })
                : Channel.CreateUnbounded<T>();
    }

    // <inheritdoc/>
    public ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        return this.channel.Writer.WriteAsync(item, cancellationToken);
    }

    // <inheritdoc/>
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        this.channel.Writer.Complete();

        return ValueTask.CompletedTask;
    }

    // <inheritdoc/>
    public ValueTask FailAsync(Exception ex, CancellationToken cancellationToken = default)
    {
        this.channel.Writer.TryComplete(ex);

        return ValueTask.CompletedTask;
    }

    // <inheritdoc/>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return this.channel.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }
}