namespace TinyBoss.Platform.Windows;

public sealed record TerminalCollectCandidate<T>(
    T Id,
    string Monitor,
    int? ExistingSlot = null,
    int SpatialOrder = 0);

public sealed record TerminalCollectPlan<T>(
    IReadOnlyDictionary<string, IReadOnlyList<T>> Assigned,
    IReadOnlyList<T> Unmanaged);

public static class TerminalCollectPlanner
{
    public const int MaxWindowsPerMonitor = PageMovePlanner.MaxWindowsPerGrid;

    public static TerminalCollectPlan<T> Plan<T>(
        IReadOnlyList<string> monitors,
        IReadOnlyList<TerminalCollectCandidate<T>> candidates,
        int maxPerMonitor = MaxWindowsPerMonitor)
        where T : notnull
    {
        if (maxPerMonitor < 0)
            maxPerMonitor = 0;

        var monitorOrder = monitors
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var enabled = monitorOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigned = monitorOrder.ToDictionary(
            monitor => monitor,
            _ => new List<T>(),
            StringComparer.OrdinalIgnoreCase);
        var unmanaged = new List<T>();
        var overflow = new Queue<T>();
        var seen = new HashSet<T>();

        foreach (var candidate in candidates)
        {
            if (!enabled.Contains(candidate.Monitor))
            {
                if (seen.Add(candidate.Id))
                    unmanaged.Add(candidate.Id);
                continue;
            }

            seen.Add(candidate.Id);
        }

        foreach (var monitor in monitorOrder)
        {
            var local = candidates
                .Where(c => enabled.Contains(c.Monitor) && string.Equals(c.Monitor, monitor, StringComparison.OrdinalIgnoreCase))
                .GroupBy(c => c.Id)
                .Select(g => g.OrderBy(c => c.ExistingSlot is null ? 1 : 0)
                    .ThenBy(c => c.ExistingSlot ?? int.MaxValue)
                    .ThenBy(c => c.SpatialOrder)
                    .First())
                .OrderBy(c => c.ExistingSlot is null ? 1 : 0)
                .ThenBy(c => c.ExistingSlot ?? int.MaxValue)
                .ThenBy(c => c.SpatialOrder)
                .ToArray();

            foreach (var candidate in local)
            {
                if (assigned[monitor].Count < maxPerMonitor)
                    assigned[monitor].Add(candidate.Id);
                else
                    overflow.Enqueue(candidate.Id);
            }
        }

        while (overflow.Count > 0)
        {
            var id = overflow.Dequeue();
            var target = monitorOrder
                .Where(m => assigned[m].Count < maxPerMonitor)
                .OrderBy(m => assigned[m].Count)
                .ThenBy(m => Array.IndexOf(monitorOrder, m))
                .FirstOrDefault();

            if (target is null)
            {
                unmanaged.Add(id);
                continue;
            }

            assigned[target].Add(id);
        }

        return new TerminalCollectPlan<T>(
            assigned.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<T>)kv.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            unmanaged.ToArray());
    }
}
