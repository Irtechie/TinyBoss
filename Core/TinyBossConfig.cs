using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyBoss.Core;

/// <summary>
/// Persisted configuration for TinyBoss. Atomic JSON read/write to
/// %LOCALAPPDATA%\TinyBoss\tinyboss.json.
/// </summary>
public sealed class TinyBossConfig
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TinyBoss");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "tinyboss.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── Grid ─────────────────────────────────────────────────────────────────
    /// <summary>Number of tiling panes: 2, 4, or 6. Default: 4.</summary>
    public int GridSize { get; set; } = 4;

    // ── Hotkeys (virtual key codes + modifiers) ──────────────────────────────
    public int VoiceModifiers { get; set; } = 0;      // No modifier — standalone key
    public int VoiceKey { get; set; } = 0xA5;           // Right Alt (VK_RMENU)

    public int TileModifiers { get; set; } = 0x0002 | 0x0004;   // Ctrl+Shift
    public int TileKey { get; set; } = 0x47;                     // G

    public int MovePageModifiers { get; set; } = 0x0002 | 0x0004; // Ctrl+Shift
    public int MovePageKey { get; set; } = 0x4D;                  // M

    public int RebalanceModifiers { get; set; } = 0x0002 | 0x0004; // Ctrl+Shift
    public int RebalanceKey { get; set; } = 0x52;                   // R

    // ── Audio ────────────────────────────────────────────────────────────────
    /// <summary>NAudio device ID. Null = system default.</summary>
    public string? MicDeviceId { get; set; }

    // ── Monitors ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Monitor device names where tiling is enabled. Null or empty = all monitors.
    /// Example: ["\\.\DISPLAY1", "\\.\DISPLAY3"]
    /// </summary>
    public List<string>? EnabledMonitors { get; set; }

    // ── Snap Layout Override ─────────────────────────────────────────────────
    /// <summary>
    /// When true, disables Windows 11 Snap Layouts flyout and replaces
    /// drag-to-top-edge with TinyBoss's own tiling overlay.
    /// </summary>
    public bool OverrideSnapLayouts { get; set; } = true;

    // ── Grid Layout ──────────────────────────────────────────────────────────
    /// <summary>
    /// Layout for the 6-pane grid. "2x3" = 2 rows × 3 cols (default),
    /// "3x2" = 3 rows × 2 cols.
    /// </summary>
    public string GridLayout { get; set; } = "2x3";

    // ── Whisper ──────────────────────────────────────────────────────────────
    public string ModelDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TinyBoss", "models");

    // ── Runtime ──────────────────────────────────────────────────────────────
    [JsonIgnore] public bool IsFirstRun { get; private set; }

    // ── Persistence ──────────────────────────────────────────────────────────

    public static TinyBossConfig Load()
    {
        Directory.CreateDirectory(ConfigDir);

        if (!File.Exists(ConfigPath))
            return new TinyBossConfig { IsFirstRun = true };

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<TinyBossConfig>(json, JsonOpts);
            return config ?? new TinyBossConfig { IsFirstRun = true };
        }
        catch
        {
            // Corrupt config — start fresh, back up the bad file
            try { File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true); } catch { }
            return new TinyBossConfig { IsFirstRun = true };
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(ConfigPath))
            File.Replace(tmp, ConfigPath, ConfigPath + ".bak");
        else
            File.Move(tmp, ConfigPath);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    public int NormalizedGridSize => GridSize switch
    {
        2 => 2,
        6 => 6,
        _ => 4, // default
    };
}
