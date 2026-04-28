using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class TilingCoordinatorGeometryTests
{
    [Fact]
    public void WideLayoutFillsColumnsTopToBottom()
    {
        Assert.Equal([0, 3, 1, 4, 2, 5], TilingCoordinator.GetFillOrder(6, "2x3"));
    }

    [Fact]
    public void ThreeWindowsInWideLayoutGivesOddColumnWindowDoubleHeight()
    {
        var workArea = new TilingCoordinator.RECT(0, 0, 1200, 800);

        var bounds = TilingCoordinator.GetPaneBoundsForOccupiedSlots(
            nint.Zero,
            gridSize: 4,
            workArea,
            layout: "2x3",
            occupiedSlots: [0, 2, 1]);

        Assert.Equal(600, bounds[1].Left);
        Assert.Equal(0, bounds[1].Top);
        Assert.Equal(1200, bounds[1].Right);
        Assert.Equal(800, bounds[1].Bottom);
    }

    [Fact]
    public void FiveWindowsInWideLayoutGivesOddColumnWindowDoubleHeight()
    {
        var workArea = new TilingCoordinator.RECT(0, 0, 1500, 800);

        var bounds = TilingCoordinator.GetPaneBoundsForOccupiedSlots(
            nint.Zero,
            gridSize: 6,
            workArea,
            layout: "2x3",
            occupiedSlots: [0, 3, 1, 4, 2]);

        Assert.Equal(1000, bounds[2].Left);
        Assert.Equal(0, bounds[2].Top);
        Assert.Equal(1500, bounds[2].Right);
        Assert.Equal(800, bounds[2].Bottom);
    }

    [Fact]
    public void FullWideLayoutDoesNotSpanBottomWindow()
    {
        var workArea = new TilingCoordinator.RECT(0, 0, 1500, 800);

        var bounds = TilingCoordinator.GetPaneBoundsForOccupiedSlots(
            nint.Zero,
            gridSize: 6,
            workArea,
            layout: "2x3",
            occupiedSlots: [0, 1, 2, 3, 4, 5]);

        Assert.Equal(500, bounds[4].Left);
        Assert.Equal(400, bounds[4].Top);
        Assert.Equal(1000, bounds[4].Right);
        Assert.Equal(800, bounds[4].Bottom);
    }
}
