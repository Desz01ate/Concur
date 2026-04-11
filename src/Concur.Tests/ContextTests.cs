namespace Concur.Tests;

using System;
using System.Threading;
using Xunit;

public class ContextTests
{
    [Fact]
    public void CancelingParent_CancelsChild()
    {
        using var parent = Context.Background.CreateChild("parent");
        using var child = parent.CreateChild("child");

        var canceled = parent.TryCancel(new OperationCanceledException("stop"));

        Assert.True(canceled);
        Assert.True(SpinWait.SpinUntil(() => child.IsCancellationRequested, TimeSpan.FromSeconds(1)));
        Assert.True(child.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelingChild_DoesNotCancelParent()
    {
        using var parent = Context.Background.CreateChild("parent");
        using var child = parent.CreateChild("child");

        var canceled = child.TryCancel(new OperationCanceledException("child-only"));

        Assert.True(canceled);
        Assert.True(child.IsCancellationRequested);
        Assert.False(parent.IsCancellationRequested);
    }

    [Fact]
    public void WithTimeout_CancelsAfterDeadline()
    {
        using var context = Context.Background.WithTimeout(TimeSpan.FromMilliseconds(50), "timeout");

        Assert.True(SpinWait.SpinUntil(() => context.IsCancellationRequested, TimeSpan.FromSeconds(5)));
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.NotNull(context.Deadline);
    }

    [Fact]
    public void WithCancel_WithExternalToken_CancelsChild()
    {
        using var cts = new CancellationTokenSource();
        using var context = Context.Background.WithCancel(cts.Token, "external");

        cts.Cancel();

        Assert.True(SpinWait.SpinUntil(() => context.IsCancellationRequested, TimeSpan.FromSeconds(1)));
        var cause = Assert.IsType<OperationCanceledException>(context.CancellationCause);
        Assert.Equal(cts.Token, cause.CancellationToken);
    }

    [Fact]
    public void WithCancel_WithMultipleExternalTokens_CancelingAnyTokenCancelsChild()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var context = Context.Background.WithCancel(new[] { first.Token, second.Token }, "multi");

        second.Cancel();

        Assert.True(SpinWait.SpinUntil(() => context.IsCancellationRequested, TimeSpan.FromSeconds(1)));
        var cause = Assert.IsType<OperationCanceledException>(context.CancellationCause);
        Assert.Equal(second.Token, cause.CancellationToken);
    }

    [Fact]
    public void WithCancel_WithMultipleExternalTokens_FirstCancellationWins()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var context = Context.Background.WithCancel(new[] { first.Token, second.Token }, "first-wins");

        first.Cancel();
        Assert.True(SpinWait.SpinUntil(() => context.IsCancellationRequested, TimeSpan.FromSeconds(1)));

        second.Cancel();

        var cause = Assert.IsType<OperationCanceledException>(context.CancellationCause);
        Assert.Equal(first.Token, cause.CancellationToken);
    }

    [Fact]
    public void WithCancel_WithPreCanceledToken_CancelsImmediately()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var context = Context.Background.WithCancel(cts.Token, "pre-canceled");

        Assert.True(context.IsCancellationRequested);
        var cause = Assert.IsType<OperationCanceledException>(context.CancellationCause);
        Assert.Equal(cts.Token, cause.CancellationToken);
    }
}
