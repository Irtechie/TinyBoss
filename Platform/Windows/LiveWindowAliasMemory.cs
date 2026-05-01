using System.Globalization;
using System.Text.Json;

namespace TinyBoss.Platform.Windows;

public sealed class LiveWindowAliasMemory
{
    private readonly Dictionary<nint, string> _aliases = new();
    private readonly string _persistPath;

    public bool Any => _aliases.Count > 0;

    public LiveWindowAliasMemory(string? persistPath = null)
    {
        var dir = persistPath is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TinyBoss")
            : Path.GetDirectoryName(persistPath);

        if (string.IsNullOrWhiteSpace(dir))
            dir = Environment.CurrentDirectory;

        Directory.CreateDirectory(dir);
        _persistPath = persistPath ?? Path.Combine(dir, "window-aliases.json");
        Load();
    }

    public void Set(nint hwnd, string? alias)
    {
        if (hwnd == nint.Zero)
            return;

        if (string.IsNullOrWhiteSpace(alias))
        {
            _aliases.Remove(hwnd);
            Persist();
            return;
        }

        _aliases[hwnd] = alias.Trim();
        Persist();
    }

    public string? Get(nint hwnd) =>
        _aliases.TryGetValue(hwnd, out var alias) ? alias : null;

    public bool Contains(nint hwnd) => _aliases.ContainsKey(hwnd);

    public void Remove(nint hwnd)
    {
        if (_aliases.Remove(hwnd))
            Persist();
    }

    public void Prune(Func<nint, bool> isLive)
    {
        var changed = false;
        foreach (var hwnd in _aliases.Keys.Where(hwnd => !isLive(hwnd)).ToList())
        {
            _aliases.Remove(hwnd);
            changed = true;
        }

        if (changed)
            Persist();
    }

    private void Load()
    {
        if (!File.Exists(_persistPath))
            return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (stored is null)
                return;

            foreach (var (key, alias) in stored)
            {
                if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hwnd) &&
                    hwnd != 0 &&
                    !string.IsNullOrWhiteSpace(alias))
                {
                    _aliases[new nint(hwnd)] = alias.Trim();
                }
            }
        }
        catch
        {
            _aliases.Clear();
        }
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var stored = _aliases.ToDictionary(
                kv => kv.Key.ToString(),
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch
        {
            // Best effort only; the live grid still has the in-memory alias.
        }
    }
}
