namespace TinyBoss.Platform.Windows;

public sealed class LiveWindowAliasMemory
{
    private readonly Dictionary<nint, string> _aliases = new();

    public bool Any => _aliases.Count > 0;

    public void Set(nint hwnd, string? alias)
    {
        if (hwnd == nint.Zero)
            return;

        if (string.IsNullOrWhiteSpace(alias))
        {
            _aliases.Remove(hwnd);
            return;
        }

        _aliases[hwnd] = alias.Trim();
    }

    public string? Get(nint hwnd) =>
        _aliases.TryGetValue(hwnd, out var alias) ? alias : null;

    public bool Contains(nint hwnd) => _aliases.ContainsKey(hwnd);

    public void Remove(nint hwnd) => _aliases.Remove(hwnd);

    public void Prune(Func<nint, bool> isLive)
    {
        foreach (var hwnd in _aliases.Keys.Where(hwnd => !isLive(hwnd)).ToList())
            _aliases.Remove(hwnd);
    }
}
