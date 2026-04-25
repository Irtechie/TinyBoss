using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TinyBoss.Core;
using TinyBoss.Platform.Windows;
using TinyBoss.Voice;
using static TinyBoss.Platform.Windows.TilingCoordinator;

namespace TinyBoss;

public class App : Application
{
    private TrayIcon? _trayIcon;
    private SessionRegistry? _registry;
    private VoiceController? _voice;
    private TilingCoordinator? _tiling;
    private DragWatcher? _dragWatcher;
    private HotKeyListener? _hotkeys;
    private TinyBossConfig? _config;
    private TileOverlay? _overlay;
    private string? _voiceTargetSessionId;
    private string? _iconPath;

    // Tiling state
    private nint _currentMonitor;
    private Dictionary<int, RECT>? _currentPaneBounds;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _config = TinyBossServices.Provider.GetRequiredService<TinyBossConfig>();
            _registry = TinyBossServices.Provider.GetRequiredService<SessionRegistry>();
            _registry.SessionChanged += OnSessionChanged;

            _voice = TinyBossServices.Provider.GetRequiredService<VoiceController>();
            _voice.RecordingStateChanged += OnRecordingStateChanged;
            _voice.StatusMessage += OnVoiceStatusMessage;

            _tiling = TinyBossServices.Provider.GetRequiredService<TilingCoordinator>();
            _tiling.SlotsChanged += OnSlotsChanged;

            _dragWatcher = TinyBossServices.Provider.GetRequiredService<DragWatcher>();
            _dragWatcher.DragStarted += OnDragStarted;
            _dragWatcher.DragMoved += OnDragMoved;
            _dragWatcher.DragEnded += OnDragEnded;

            _hotkeys = TinyBossServices.Provider.GetRequiredService<HotKeyListener>();
            _hotkeys.TileKeyPressed += OnTileKeyPressed;
            _hotkeys.RebalanceKeyPressed += OnRebalanceKeyPressed;
            _hotkeys.OverlayDismiss += OnOverlayDismiss;

            SetupTrayIcon();

            // Start voice and drag detection
            _voice.Start();
            // DragWatcher needs a message pump thread — install on UI thread
            _dragWatcher.Install();

            // First-run: show settings for mic selection
            CheckFirstRun();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ── Tray icon ────────────────────────────────────────────────────────────

    private void SetupTrayIcon()
    {
        _iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TinyBoss.ico");

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(_iconPath),
            ToolTipText = "TinyBoss",
            Menu = BuildTrayMenu(),
            IsVisible = true,
        };
    }

    private void OnSessionChanged(SessionEvent evt) =>
        Dispatcher.UIThread.Post(RebuildTrayMenu);

    private void OnSlotsChanged() =>
        Dispatcher.UIThread.Post(RebuildTrayMenu);

    private void OnRecordingStateChanged(bool recording)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon is null) return;
            _trayIcon.ToolTipText = recording ? "TinyBoss 🔴 Recording..." : "TinyBoss";
        });
    }

    private void OnVoiceStatusMessage(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon is null) return;
            _trayIcon.ToolTipText = $"TinyBoss — {message}";
            Task.Delay(3000).ContinueWith(_ =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (_trayIcon is not null && !(_voice?.IsRecording ?? false))
                        _trayIcon.ToolTipText = "TinyBoss";
                }));
        });
    }

    private void RebuildTrayMenu()
    {
        if (_trayIcon is null) return;
        _trayIcon.Menu = BuildTrayMenu();
    }

    private NativeMenu BuildTrayMenu()
    {
        var menu = new NativeMenu();
        var sessions = _registry?.GetSnapshot() ?? [];

        // Voice target selection
        var defaultItem = new NativeMenuItem("🎯 Default (focused window)")
        {
            ToggleType = NativeMenuItemToggleType.Radio,
            IsChecked = _voiceTargetSessionId is null,
        };
        defaultItem.Click += (_, _) =>
        {
            _voiceTargetSessionId = null;
            _voice?.SetVoiceTarget(null);
            RebuildTrayMenu();
        };
        menu.Add(defaultItem);

        foreach (var s in sessions)
        {
            var sid = s.SessionId;
            var item = new NativeMenuItem($"📋 {s.Command} (PID {s.Process.Id})")
            {
                ToggleType = NativeMenuItemToggleType.Radio,
                IsChecked = _voiceTargetSessionId == sid,
            };
            item.Click += (_, _) =>
            {
                _voiceTargetSessionId = sid;
                _voice?.SetVoiceTarget(sid);
                RebuildTrayMenu();
            };
            menu.Add(item);
        }

        menu.Add(new NativeMenuItemSeparator());

        // Tiling actions
        var tileItem = new NativeMenuItem($"📐 Tile Windows (auto {_tiling?.GridSize ?? 0}-pane)");
        tileItem.Click += (_, _) => OnTileKeyPressed();
        menu.Add(tileItem);

        var rebalanceItem = new NativeMenuItem("🔄 Rebalance Grid");
        rebalanceItem.Click += (_, _) => OnRebalanceKeyPressed();
        menu.Add(rebalanceItem);

        menu.Add(new NativeMenuItemSeparator());

        // Tiled windows — rename entries
        var tiledSnapshot = _tiling?.GetSnapshot() ?? new Dictionary<int, TileSlot>();
        if (tiledSnapshot.Count > 0)
        {
            foreach (var (slot, ts) in tiledSnapshot.OrderBy(kv => kv.Key))
            {
                var label = ts.Alias ?? $"Window (slot {slot + 1})";
                var capturedSlot = slot;
                var renameItem = new NativeMenuItem($"✏️ {label}");
                renameItem.Click += (_, _) => ShowRenameDialog(capturedSlot, ts.Alias ?? "");
                menu.Add(renameItem);
            }
            menu.Add(new NativeMenuItemSeparator());
        }

        var settingsItem = new NativeMenuItem("⚙️ Settings");
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Add(settingsItem);

        var quitItem = new NativeMenuItem("Quit TinyBoss");
        quitItem.Click += (_, _) =>
        {
            _voice?.Dispose();
            _dragWatcher?.Dispose();
            _tiling?.Dispose();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                Dispatcher.UIThread.Post(() => desktop.TryShutdown(0));
        };
        menu.Add(quitItem);

        return menu;
    }

    // ── Tiling orchestration ─────────────────────────────────────────────────

    private void OnTileKeyPressed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_overlay is not null && _overlay.IsVisible)
            {
                DismissOverlay();
                return;
            }

            ShowOverlay();
        });
    }

    private void OnRebalanceKeyPressed()
    {
        if (_tiling is null) return;
        var monitor = TilingCoordinator.GetMonitorAtCursor();
        _tiling.Rebalance(monitor);
    }

    private void OnOverlayDismiss() =>
        Dispatcher.UIThread.Post(DismissOverlay);

    private void ShowOverlay()
    {
        _currentMonitor = TilingCoordinator.GetMonitorAtCursor();
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        TilingCoordinator.GetMonitorInfo(_currentMonitor, ref info);

        var gridSize = _tiling?.GridSize ?? 1;
        _currentPaneBounds = TilingCoordinator.GetPaneBounds(_currentMonitor, gridSize);

        _overlay = new TileOverlay();
        _overlay.SetMonitorBounds(info.rcWork, info.rcMonitor);
        _overlay.SetGridSize(gridSize);

        // Mark occupied slots with aliases
        var snapshot = _tiling?.GetSnapshot() ?? new Dictionary<int, TileSlot>();
        var aliases = snapshot.Where(kv => kv.Value.Alias is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Alias!);
        _overlay.SetOccupiedSlots(new HashSet<int>(snapshot.Keys), aliases);

        _overlay.DismissRequested += () => Dispatcher.UIThread.Post(DismissOverlay);
        _overlay.RenameRequested += slot =>
        {
            var snapshot = _tiling?.GetSnapshot();
            var currentAlias = snapshot?.TryGetValue(slot, out var ts) == true ? ts.Alias ?? "" : "";
            ShowRenameDialog(slot, currentAlias);
        };
        _overlay.Show();
        _hotkeys?.SetOverlayActive(true);
    }

    private void DismissOverlay()
    {
        _hotkeys?.SetOverlayActive(false);
        _overlay?.Close();
        _overlay = null;
        _currentPaneBounds = null;
    }

    // ── Drag events → overlay highlighting + snap ────────────────────────────

    private void OnDragStarted(nint hwnd)
    {
        // If overlay is already showing, just track. Otherwise, no auto-show.
    }

    private void OnDragMoved(int screenX, int screenY)
    {
        if (_overlay is null || !_overlay.IsVisible || _currentPaneBounds is null) return;

        var slot = TilingCoordinator.HitTestSlot(screenX, screenY, _currentPaneBounds);
        Dispatcher.UIThread.Post(() => _overlay?.HighlightZone(slot));
    }

    private void OnDragEnded(nint hwnd, int screenX, int screenY)
    {
        if (_overlay is null || !_overlay.IsVisible || _currentPaneBounds is null) return;

        // Terminal-only filter
        if (!TerminalDetector.IsTerminalWindow(hwnd))
            return;

        var slot = TilingCoordinator.HitTestSlot(screenX, screenY, _currentPaneBounds);
        if (slot >= 0 && _tiling is not null)
        {
            // If dropped on occupied slot, auto-assign to first empty slot
            if (_tiling.IsSlotOccupied(slot))
            {
                var empty = _tiling.FindNextEmptySlot();
                if (empty < 0) return; // at capacity
                slot = empty;
            }

            // Find session ID if this is a managed session
            string? sessionId = null;
            if (_registry is not null)
            {
                TilingCoordinator.GetWindowThreadProcessId(hwnd, out int pid);
                var session = _registry.GetSnapshot().FirstOrDefault(s => s.Process.Id == pid);
                sessionId = session?.SessionId;
            }

            _tiling.AssignToSlot(slot, hwnd, sessionId);
            _tiling.PositionAll(_currentMonitor); // reposition all with new grid size

            // Update overlay
            Dispatcher.UIThread.Post(() =>
            {
                var snapshot = _tiling.GetSnapshot();
                var gridSize = _tiling.GridSize;
                _overlay?.SetGridSize(gridSize);
                var aliases = snapshot.Where(kv => kv.Value.Alias is not null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.Alias!);
                _overlay?.SetOccupiedSlots(new HashSet<int>(snapshot.Keys), aliases);
                _overlay?.HighlightZone(-1);

                // Refresh pane bounds with new grid size
                _currentPaneBounds = TilingCoordinator.GetPaneBounds(_currentMonitor, gridSize);

                // Auto-dismiss if all slots filled (6 max)
                if (snapshot.Count >= 6)
                    DismissOverlay();
            });
        }
    }

    // ── Settings (Phase 4) ─────────────────────────────────────────────────

    private SettingsWindow? _settingsWindow;

    private void ShowSettings()
    {
        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_config!);
        _settingsWindow.SettingsSaved += OnSettingsSaved;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnSettingsSaved()
    {
        if (_config is null) return;
        _hotkeys?.RequestReRegister();
        RebuildTrayMenu();
    }

    private void ShowRenameDialog(int slot, string currentAlias)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var dialog = new RenameDialog(currentAlias);
            dialog.Closed += (_, _) =>
            {
                if (dialog.AliasResult is { } alias && _tiling is not null)
                {
                    _tiling.RenameSlot(slot, alias);
                }
            };
            dialog.Show();
        });
    }

    private void CheckFirstRun()
    {
        if (_config?.IsFirstRun == true)
        {
            ShowSettings();
            _config.Save(); // Creates the file so next launch isn't first-run
        }
    }
}
