using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TinyBoss.Core;
using TinyBoss.Platform.Windows;
using NAudio.CoreAudioApi;

namespace TinyBoss;

public sealed record HotkeyPreset(string Label, int Modifiers, int Key);

public partial class SettingsWindow : Window
{
    private readonly TinyBossConfig _config;
    private readonly ComboBox _voiceHotkeyCombo;
    private readonly ComboBox _tileHotkeyCombo;
    private readonly ComboBox _movePageHotkeyCombo;
    private readonly ComboBox _micCombo;
    private readonly ComboBox _gridLayoutCombo;
    private readonly StackPanel _monitorList;
    private readonly CheckBox _overrideSnapCheck;
    private readonly TextBlock _conflictWarning;
    private readonly List<(CheckBox Check, string DeviceName)> _monitorChecks = new();

    private static readonly HotkeyPreset[] VoicePresets =
    [
        new("Right Alt (hold)", 0, 0xA5),
        new("Shift + ` (~)", 0x0004, 0xC0),
        new("Ctrl+Shift+Space", 0x0006, 0x20),
    ];

    private static readonly HotkeyPreset[] TilePresets =
    [
        new("Ctrl+Shift+G", 0x0006, 0x47),
        new("Shift + ` (~)", 0x0004, 0xC0),
        new("Ctrl+Shift+Space", 0x0006, 0x20),
        new("Win + `", 0x0008, 0xC0),
    ];

    private static readonly HotkeyPreset[] MovePagePresets =
    [
        new("Ctrl+Shift+M", 0x0006, 0x4D),
        new("Ctrl+Alt+M", 0x0003, 0x4D),
        new("Win + M", 0x0008, 0x4D),
    ];

    public SettingsWindow() : this(new TinyBossConfig()) { }

    public SettingsWindow(TinyBossConfig config)
    {
        _config = config;
        AvaloniaXamlLoader.Load(this);

        _voiceHotkeyCombo = this.FindControl<ComboBox>("VoiceHotkeyCombo")!;
        _tileHotkeyCombo = this.FindControl<ComboBox>("TileHotkeyCombo")!;
        _movePageHotkeyCombo = this.FindControl<ComboBox>("MovePageHotkeyCombo")!;
        _micCombo = this.FindControl<ComboBox>("MicCombo")!;
        _gridLayoutCombo = this.FindControl<ComboBox>("GridLayoutCombo")!;
        _monitorList = this.FindControl<StackPanel>("MonitorList")!;
        _overrideSnapCheck = this.FindControl<CheckBox>("OverrideSnapCheck")!;
        _conflictWarning = this.FindControl<TextBlock>("ConflictWarning")!;

        var saveButton = this.FindControl<Button>("SaveButton")!;
        var cancelButton = this.FindControl<Button>("CancelButton")!;

        saveButton.Click += OnSaveClick;
        cancelButton.Click += OnCancelClick;

        _voiceHotkeyCombo.SelectionChanged += (_, _) => CheckConflicts();
        _tileHotkeyCombo.SelectionChanged += (_, _) => CheckConflicts();
        _movePageHotkeyCombo.SelectionChanged += (_, _) => CheckConflicts();
        _gridLayoutCombo.SelectionChanged += OnGridLayoutChanged;

        LoadCurrentSettings();
    }

    /// <summary>Fires when settings are saved so the app can apply changes.</summary>
    public event Action? SettingsSaved;

    private void LoadCurrentSettings()
    {
        // Voice hotkey presets
        PopulatePresets(_voiceHotkeyCombo, VoicePresets, _config.VoiceModifiers, _config.VoiceKey);

        // Tile hotkey presets
        PopulatePresets(_tileHotkeyCombo, TilePresets, _config.TileModifiers, _config.TileKey);

        // Move-page hotkey presets
        PopulatePresets(_movePageHotkeyCombo, MovePagePresets, _config.MovePageModifiers, _config.MovePageKey);

        // Microphone enumeration
        _micCombo.Items.Clear();
        var defaultItem = new ComboBoxItem { Content = "System Default", Tag = (string?)null };
        _micCombo.Items.Add(defaultItem);
        _micCombo.SelectedIndex = 0;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            int selectedIdx = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                var dev = devices[i];
                var item = new ComboBoxItem { Content = dev.FriendlyName, Tag = dev.ID };
                _micCombo.Items.Add(item);
                if (_config.MicDeviceId == dev.ID)
                    selectedIdx = i + 1;
            }
            if (selectedIdx > 0)
                _micCombo.SelectedIndex = selectedIdx;
        }
        catch
        {
            // No audio devices — leave just "System Default"
        }

        // Monitor enumeration
        LoadMonitors();

        // Snap Layout override
        _overrideSnapCheck.IsChecked = _config.OverrideSnapLayouts;

        // Grid layout
        _gridLayoutCombo.Items.Clear();
        _gridLayoutCombo.Items.Add(new ComboBoxItem { Content = "2 rows × 3 columns (wide)", Tag = "2x3" });
        _gridLayoutCombo.Items.Add(new ComboBoxItem { Content = "3 rows × 2 columns (tall)", Tag = "3x2" });
        _gridLayoutCombo.SelectedIndex = _config.GridLayout == "3x2" ? 1 : 0;
    }

    private void LoadMonitors()
    {
        _monitorList.Children.Clear();
        _monitorChecks.Clear();

        try
        {
            var monitors = MonitorEnumerator.GetMonitors();
            bool allEnabled = _config.EnabledMonitors is null || _config.EnabledMonitors.Count == 0;

            foreach (var mon in monitors)
            {
                var primary = mon.IsPrimary ? " ⭐" : "";
                var displayName = string.Equals(mon.FriendlyName, mon.DeviceName, StringComparison.OrdinalIgnoreCase)
                    ? MonitorEnumerator.FormatDisplayName(mon.DeviceName)
                    : mon.FriendlyName;
                var label = $"{displayName} — {mon.Width}×{mon.Height}{primary}";
                var cb = new CheckBox
                {
                    Content = label,
                    IsChecked = allEnabled || _config.EnabledMonitors!.Contains(mon.DeviceName),
                    Tag = mon.DeviceName,
                };
                _monitorChecks.Add((cb, mon.DeviceName));
                _monitorList.Children.Add(cb);
            }

            if (monitors.Count == 0)
            {
                _monitorList.Children.Add(new TextBlock
                {
                    Text = "No monitors detected",
                    Foreground = Avalonia.Media.Brushes.Gray,
                    FontStyle = Avalonia.Media.FontStyle.Italic,
                });
            }
        }
        catch
        {
            _monitorList.Children.Add(new TextBlock
            {
                Text = "Could not enumerate monitors",
                Foreground = Avalonia.Media.Brushes.Gray,
            });
        }
    }

    private static void PopulatePresets(ComboBox combo, HotkeyPreset[] presets, int currentMods, int currentKey)
    {
        combo.Items.Clear();
        int selectedIdx = 0;
        for (int i = 0; i < presets.Length; i++)
        {
            var p = presets[i];
            combo.Items.Add(new ComboBoxItem { Content = p.Label, Tag = p });
            if (p.Modifiers == currentMods && p.Key == currentKey)
                selectedIdx = i;
        }
        combo.SelectedIndex = selectedIdx;
    }

    private void CheckConflicts()
    {
        var voice = GetSelectedPreset(_voiceHotkeyCombo);
        var tile = GetSelectedPreset(_tileHotkeyCombo);
        var movePage = GetSelectedPreset(_movePageHotkeyCombo);
        if (HasHotkeyConflict(voice, tile, movePage))
        {
            _conflictWarning.Text = "⚠ Voice, Tile, and Move Page hotkeys cannot be the same.";
            _conflictWarning.IsVisible = true;
        }
        else
        {
            _conflictWarning.IsVisible = false;
        }
    }

    private static HotkeyPreset? GetSelectedPreset(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag as HotkeyPreset;
    }

    public static bool HasHotkeyConflict(params HotkeyPreset?[] presets)
    {
        var selected = presets.Where(p => p is not null).Cast<HotkeyPreset>().ToArray();
        return selected
            .GroupBy(p => (p.Modifiers, p.Key))
            .Any(g => g.Count() > 1);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // Check for conflicts
        var voice = GetSelectedPreset(_voiceHotkeyCombo);
        var tile = GetSelectedPreset(_tileHotkeyCombo);
        var movePage = GetSelectedPreset(_movePageHotkeyCombo);
        if (HasHotkeyConflict(voice, tile, movePage))
        {
            _conflictWarning.Text = "⚠ Voice, Tile, and Move Page hotkeys cannot be the same. Pick different keys.";
            _conflictWarning.IsVisible = true;
            return;
        }

        // Voice hotkey
        if (voice is not null)
        {
            _config.VoiceModifiers = voice.Modifiers;
            _config.VoiceKey = voice.Key;
        }

        // Tile hotkey
        if (tile is not null)
        {
            _config.TileModifiers = tile.Modifiers;
            _config.TileKey = tile.Key;
        }

        // Move-page hotkey
        if (movePage is not null)
        {
            _config.MovePageModifiers = movePage.Modifiers;
            _config.MovePageKey = movePage.Key;
        }

        // Microphone
        if (_micCombo.SelectedItem is ComboBoxItem micItem)
            _config.MicDeviceId = micItem.Tag as string;

        // Monitors — save checked ones (null = all enabled)
        var enabled = _monitorChecks
            .Where(mc => mc.Check.IsChecked == true)
            .Select(mc => mc.DeviceName)
            .ToList();

        if (enabled.Count == _monitorChecks.Count || enabled.Count == 0)
            _config.EnabledMonitors = null; // all monitors
        else
            _config.EnabledMonitors = enabled;

        // Snap Layout override
        _config.OverrideSnapLayouts = _overrideSnapCheck.IsChecked == true;
        SnapLayoutControl.Apply(_config.OverrideSnapLayouts);

        // Grid layout
        if (_gridLayoutCombo.SelectedItem is ComboBoxItem layoutItem)
            _config.GridLayout = layoutItem.Tag as string ?? "2x3";

        _config.Save();
        SettingsSaved?.Invoke();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnGridLayoutChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_gridLayoutCombo.SelectedItem is ComboBoxItem layoutItem)
        {
            _config.GridLayout = layoutItem.Tag as string ?? "2x3";
            _config.Save();
            SettingsSaved?.Invoke();
        }
    }
}
