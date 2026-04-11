namespace Concur;

using System.Threading;

/// <summary>
/// Represents a cancellable operation scope that can derive child scopes.
/// </summary>
public sealed class Context : IDisposable
{
    private readonly CancellationTokenSource cts;
    private readonly bool canCancel;
    private CancellationTokenRegistration parentRegistration;
    private List<CancellationTokenRegistration>? linkedTokenRegistrations;
    private Timer? deadlineTimer;
    private int disposeState;
    private Exception? cancellationCause;

    private Context(
        Context? parent,
        string? operationName,
        DateTimeOffset? deadline,
        bool canCancel)
    {
        this.Parent = parent;
        this.OperationName = operationName;
        this.Deadline = deadline;
        this.canCancel = canCancel;
        this.cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Gets the uncancelled root context.
    /// </summary>
    public static Context Background { get; } = new(parent: null, operationName: null, deadline: null, canCancel: false);

    /// <summary>
    /// Gets the parent context, if any.
    /// </summary>
    public Context? Parent { get; }

    /// <summary>
    /// Gets the operation name associated with this context.
    /// </summary>
    public string? OperationName { get; }

    /// <summary>
    /// Gets the deadline for this context, if any.
    /// </summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>
    /// Gets the cancellation token for this context.
    /// </summary>
    public CancellationToken CancellationToken => this.cts.Token;

    /// <summary>
    /// Gets the first observed cancellation cause.
    /// </summary>
    public Exception? CancellationCause => this.cancellationCause;

    /// <summary>
    /// Gets a value indicating whether cancellation has been requested.
    /// </summary>
    public bool IsCancellationRequested => this.cts.IsCancellationRequested;

    /// <summary>
    /// Creates a child context linked to this context.
    /// </summary>
    /// <param name="operationName">The child operation name.</param>
    /// <returns>A new linked child context.</returns>
    public Context CreateChild(string? operationName = null)
    {
        return this.WithCancel(operationName);
    }

    /// <summary>
    /// Creates a cancellable child context linked to this context.
    /// </summary>
    /// <param name="operationName">The child operation name.</param>
    /// <returns>A new linked child context.</returns>
    public Context WithCancel(string? operationName = null)
    {
        return CreateLinkedContext(this, operationName, deadline: null, dueTime: null, linkedTokens: null);
    }

    /// <summary>
    /// Creates a cancellable child context linked to this context and an external cancellation token.
    /// </summary>
    /// <param name="cancellationToken">The external cancellation token to link.</param>
    /// <param name="operationName">The child operation name.</param>
    /// <returns>A new linked child context.</returns>
    public Context WithCancel(CancellationToken cancellationToken, string? operationName = null)
    {
        return CreateLinkedContext(this, operationName, deadline: null, dueTime: null, linkedTokens: [cancellationToken]);
    }

    /// <summary>
    /// Creates a cancellable child context linked to this context and all external cancellation tokens.
    /// </summary>
    /// <param name="cancellationTokens">The external cancellation tokens to link.</param>
    /// <param name="operationName">The child operation name.</param>
    /// <returns>A new linked child context.</returns>
    public Context WithCancel(IEnumerable<CancellationToken> cancellationTokens, string? operationName = null)
    {
        ArgumentNullException.ThrowIfNull(cancellationTokens);
        return CreateLinkedContext(this, operationName, deadline: null, dueTime: null, linkedTokens: cancellationTokens);
    }

    /// <summary>
    /// Creates a child context that cancels after the given timeout.
    /// </summary>
    /// <param name="timeout">The timeout for the child context.</param>
    /// <param name="operationName">The child operation name.</param>
    /// <returns>A new linked child context.</returns>
    public Context WithTimeout(TimeSpan timeout, string? operationName = null)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return this.CreateChild(operationName);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        return CreateLinkedContext(this, operationName, DateTimeOffset.UtcNow.Add(timeout), timeout, linkedTokens: null);
    }

    /// <summary>
    /// Creates a child context that cancels at the given deadline.
    /// </summary>
    /// <param name="deadline">The deadline for the child context.</param>
    /// <param name="operationName">The child operation name.</param>
    /// <returns>A new linked child context.</returns>
    public Context WithDeadline(DateTimeOffset deadline, string? operationName = null)
    {
        var dueTime = deadline - DateTimeOffset.UtcNow;
        if (dueTime < TimeSpan.Zero)
        {
            dueTime = TimeSpan.Zero;
        }

        return CreateLinkedContext(this, operationName, deadline, dueTime, linkedTokens: null);
    }

    /// <summary>
    /// Requests cancellation for this context.
    /// </summary>
    /// <param name="cause">The cancellation cause.</param>
    /// <returns><see langword="true"/> when cancellation was requested by this call; otherwise <see langword="false"/>.</returns>
    public bool TryCancel(Exception? cause = null)
    {
        if (!this.canCancel)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref this.cancellationCause, cause ?? new OperationCanceledException(this.CancellationToken), null) is not null)
        {
            return false;
        }

        try
        {
            this.cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposeState, 1) == 1)
        {
            return;
        }

        this.deadlineTimer?.Dispose();
        this.parentRegistration.Dispose();
        
        if (this.linkedTokenRegistrations is { } registrations)
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }

        if (!ReferenceEquals(this, Background))
        {
            this.cts.Dispose();
        }
    }

    private static Context CreateLinkedContext(
        Context parent,
        string? operationName,
        DateTimeOffset? deadline,
        TimeSpan? dueTime,
        IEnumerable<CancellationToken>? linkedTokens)
    {
        var child = new Context(parent, operationName, deadline, canCancel: true);

        child.parentRegistration = parent.CancellationToken.Register(static state =>
        {
            var context = (Context)state!;
            var parentCause = context.Parent?.CancellationCause;
            var cause = parentCause ?? new OperationCanceledException(context.Parent?.CancellationToken ?? CancellationToken.None);
            context.TryCancel(cause);
        }, child);

        if (linkedTokens is not null)
        {
            foreach (var linkedToken in linkedTokens)
            {
                if (!linkedToken.CanBeCanceled)
                {
                    continue;
                }

                if (linkedToken.IsCancellationRequested)
                {
                    child.TryCancel(new OperationCanceledException(linkedToken));
                    continue;
                }

                child.linkedTokenRegistrations ??= [];
                child.linkedTokenRegistrations.Add(linkedToken.Register(static state =>
                {
                    var (context, cancellationToken) = ((Context Context, CancellationToken CancellationToken))state!;
                    context.TryCancel(new OperationCanceledException(cancellationToken));
                }, (child, linkedToken)));
            }
        }

        if (dueTime is { } value)
        {
            child.deadlineTimer = new Timer(static state =>
            {
                var context = (Context)state!;
                var cause = context.Deadline is { } deadlineValue
                    ? new TimeoutException($"The context deadline '{deadlineValue:O}' has elapsed.")
                    : new TimeoutException("The context timeout has elapsed.");
                context.TryCancel(cause);
            }, child, value, Timeout.InfiniteTimeSpan);
        }

        return child;
    }
}
