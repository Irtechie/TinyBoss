using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class PageMovePlannerTests
{
    [Fact]
    public void MovesSourceIntoTargetCapacity()
    {
        var plan = PageMovePlanner.Plan([1, 2], [3, 4]);

        Assert.Equal([1, 2, 3, 4], plan.Target);
        Assert.Empty(plan.Source);
        Assert.Equal([3, 4], plan.Moved);
    }

    [Fact]
    public void FillsTargetToSix()
    {
        var plan = PageMovePlanner.Plan([1, 2], [3, 4, 5, 6]);

        Assert.Equal([1, 2, 3, 4, 5, 6], plan.Target);
        Assert.Empty(plan.Source);
        Assert.Equal([3, 4, 5, 6], plan.Moved);
    }

    [Fact]
    public void KeepsOverflowOnSource()
    {
        var plan = PageMovePlanner.Plan([1, 2], [3, 4, 5, 6, 7]);

        Assert.Equal([1, 2, 3, 4, 5, 6], plan.Target);
        Assert.Equal([7], plan.Source);
        Assert.Equal([3, 4, 5, 6], plan.Moved);
    }

    [Fact]
    public void DoesNotMoveWhenTargetFull()
    {
        var plan = PageMovePlanner.Plan([1, 2, 3, 4, 5, 6], [7, 8]);

        Assert.Equal([1, 2, 3, 4, 5, 6], plan.Target);
        Assert.Equal([7, 8], plan.Source);
        Assert.Empty(plan.Moved);
    }

    [Fact]
    public void PreservesSourceOrderWhenAppending()
    {
        var plan = PageMovePlanner.Plan(["a"], ["b", "c", "d"]);

        Assert.Equal(["a", "b", "c", "d"], plan.Target);
        Assert.Equal(["b", "c", "d"], plan.Moved);
    }
}
