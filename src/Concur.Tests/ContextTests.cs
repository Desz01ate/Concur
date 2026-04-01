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

        Assert.True(SpinWait.SpinUntil(() => context.IsCancellationRequested, TimeSpan.FromSeconds(1)));
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.NotNull(context.Deadline);
    }
}
