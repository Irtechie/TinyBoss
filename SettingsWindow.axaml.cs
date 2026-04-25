using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using KittenHerder.Core;
using NAudio.CoreAudioApi;

namespace KittenHerder;

public partial class SettingsWindow : Window
{
    private readonly TinyBossConfig _config;
    private readonly ComboBox _gridSizeCombo;
    private readonly ComboBox _micCombo;
    private readonly TextBlock _hotkeyDisplay;

    public SettingsWindow() : this(new TinyBossConfig()) { }

    public SettingsWindow(TinyBossConfig config)
    {
        _config = config;
        AvaloniaXamlLoader.Load(this);

        _gridSizeCombo = this.FindControl<ComboBox>("GridSizeCombo")!;
        _micCombo = this.FindControl<ComboBox>("MicCombo")!;
        _hotkeyDisplay = this.FindControl<TextBlock>("HotkeyDisplay")!;

        var saveButton = this.FindControl<Button>("SaveButton")!;
        var cancelButton = this.FindControl<Button>("CancelButton")!;

        saveButton.Click += OnSaveClick;
        cancelButton.Click += OnCancelClick;

        LoadCurrentSettings();
    }

    /// <summary>Fires when settings are saved so the app can apply changes.</summary>
    public event Action? SettingsSaved;

    private void LoadCurrentSettings()
    {
        // Grid size
        int gridIdx = _config.NormalizedGridSize switch { 2 => 0, 6 => 2, _ => 1 };
        _gridSizeCombo.SelectedIndex = gridIdx;

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
                    selectedIdx = i + 1; // +1 for "System Default"
            }
            if (selectedIdx > 0)
                _micCombo.SelectedIndex = selectedIdx;
        }
        catch
        {
            // No audio devices — leave just "System Default"
        }

        // Hotkey display
        _hotkeyDisplay.Text = FormatHotkey(_config.VoiceModifiers, _config.VoiceKey);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // Grid size
        if (_gridSizeCombo.SelectedItem is ComboBoxItem gridItem && gridItem.Tag is string gridTag)
            _config.GridSize = int.Parse(gridTag);

        // Microphone
        if (_micCombo.SelectedItem is ComboBoxItem micItem)
            _config.MicDeviceId = micItem.Tag as string;

        _config.Save();
        SettingsSaved?.Invoke();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private static string FormatHotkey(int modifiers, int vk)
    {
        var parts = new List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");

        string keyName = vk switch
        {
            0x20 => "Space",
            0x47 => "G",
            0x52 => "R",
            _ => $"0x{vk:X2}",
        };
        parts.Add(keyName);
        return string.Join("+", parts);
    }
}
