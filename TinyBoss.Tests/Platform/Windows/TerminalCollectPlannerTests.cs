using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class TerminalCollectPlannerTests
{
    [Fact]
    public void KeepsTerminalsOnCurrentMonitorWhenCapacityAllows()
    {
        var plan = TerminalCollectPlanner.Plan<string>(
            ["A", "B"],
            [
                new TerminalCollectCandidate<string>("a1", "A", SpatialOrder: 0),
                new TerminalCollectCandidate<string>("a2", "A", SpatialOrder: 1),
                new TerminalCollectCandidate<string>("a3", "A", SpatialOrder: 2),
                new TerminalCollectCandidate<string>("b1", "B", SpatialOrder: 0),
                new TerminalCollectCandidate<string>("b2", "B", SpatialOrder: 1),
            ]);

        Assert.Equal(["a1", "a2", "a3"], plan.Assigned["A"].ToArray());
        Assert.Equal(["b1", "b2"], plan.Assigned["B"].ToArray());
        Assert.Empty(plan.Unmanaged);
    }

    [Fact]
    public void SpillsOverflowToLeastFilledMonitor()
    {
        var candidates = Enumerable.Range(1, 8)
            .Select(i => new TerminalCollectCandidate<string>($"a{i}", "A", SpatialOrder: i))
            .Append(new TerminalCollectCandidate<string>("b1", "B", SpatialOrder: 0))
            .ToArray();

        var plan = TerminalCollectPlanner.Plan(["A", "B"], candidates);

        Assert.Equal(["a1", "a2", "a3", "a4", "a5", "a6"], plan.Assigned["A"].ToArray());
        Assert.Equal(["b1", "a7", "a8"], plan.Assigned["B"].ToArray());
        Assert.Empty(plan.Unmanaged);
    }

    [Fact]
    public void LeavesExtrasUnmanagedPastTotalCapacity()
    {
        var candidates = Enumerable.Range(1, 19)
            .Select(i => new TerminalCollectCandidate<string>($"w{i}", "A", SpatialOrder: i))
            .ToArray();

        var plan = TerminalCollectPlanner.Plan(["A", "B", "C"], candidates);

        Assert.Equal(6, plan.Assigned["A"].Count);
        Assert.Equal(6, plan.Assigned["B"].Count);
        Assert.Equal(6, plan.Assigned["C"].Count);
        Assert.Equal(["w19"], plan.Unmanaged.ToArray());
    }

    [Fact]
    public void UsesMonitorOrderAsTieBreakForEquallyLeastFilledRecipients()
    {
        var candidates = Enumerable.Range(1, 8)
            .Select(i => new TerminalCollectCandidate<string>($"a{i}", "A", SpatialOrder: i))
            .ToArray();

        var plan = TerminalCollectPlanner.Plan(["A", "B", "C"], candidates);

        Assert.Equal(["a7"], plan.Assigned["B"].ToArray());
        Assert.Equal(["a8"], plan.Assigned["C"].ToArray());
    }

    [Fact]
    public void KeepsExistingManagedSlotsBeforeSpatialOnlyCandidates()
    {
        var plan = TerminalCollectPlanner.Plan<string>(
            ["A", "B"],
            [
                new TerminalCollectCandidate<string>("new1", "A", SpatialOrder: 0),
                new TerminalCollectCandidate<string>("new2", "A", SpatialOrder: 1),
                new TerminalCollectCandidate<string>("old3", "A", ExistingSlot: 3, SpatialOrder: 2),
                new TerminalCollectCandidate<string>("old0", "A", ExistingSlot: 0, SpatialOrder: 3),
                new TerminalCollectCandidate<string>("new3", "A", SpatialOrder: 4),
                new TerminalCollectCandidate<string>("new4", "A", SpatialOrder: 5),
                new TerminalCollectCandidate<string>("new5", "A", SpatialOrder: 6),
            ]);

        Assert.Equal(["old0", "old3", "new1", "new2", "new3", "new4"], plan.Assigned["A"].ToArray());
        Assert.Equal(["new5"], plan.Assigned["B"].ToArray());
    }

    [Fact]
    public void DisabledMonitorCandidatesStayUnmanaged()
    {
        var plan = TerminalCollectPlanner.Plan<string>(
            ["A"],
            [
                new TerminalCollectCandidate<string>("a1", "A", SpatialOrder: 0),
                new TerminalCollectCandidate<string>("disabled1", "Disabled", SpatialOrder: 0),
            ]);

        Assert.Equal(["a1"], plan.Assigned["A"].ToArray());
        Assert.Equal(["disabled1"], plan.Unmanaged.ToArray());
    }
}
