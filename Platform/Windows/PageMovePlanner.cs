namespace TinyBoss.Platform.Windows;

public sealed record PageMovePlan<T>(
    IReadOnlyList<T> Target,
    IReadOnlyList<T> Source,
    IReadOnlyList<T> Moved);

public static class PageMovePlanner
{
    public const int MaxWindowsPerGrid = 6;

    public static PageMovePlan<T> Plan<T>(
        IReadOnlyList<T> target,
        IReadOnlyList<T> source,
        int maxTarget = MaxWindowsPerGrid)
    {
        if (maxTarget < 0)
            maxTarget = 0;

        var targetAfter = target.Take(maxTarget).ToList();
        var capacity = Math.Max(0, maxTarget - targetAfter.Count);
        var moved = source.Take(capacity).ToList();
        var sourceAfter = source.Skip(moved.Count).ToList();

        targetAfter.AddRange(moved);
        return new PageMovePlan<T>(targetAfter, sourceAfter, moved);
    }
}
