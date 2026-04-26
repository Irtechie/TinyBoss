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
    private PageMoveContext? _pageMove;

    private sealed record PageMoveContext(string SourceDevice, nint SourceMonitor, nint Hwnd);

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
            _tiling.Layout = _config.GridLayout ?? "2x3";
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

            // Override Windows Snap Layouts if configured (restart Explorer once to apply)
            if (_config.OverrideSnapLayouts)
                SnapLayoutControl.DisableSnapLayouts(restartExplorer: false);

            RunTerminalCollection("startup");

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
        var tileItem = new NativeMenuItem("📐 Tile Windows");
        tileItem.Click += (_, _) => OnTileKeyPressed();
        menu.Add(tileItem);

        var rebalanceItem = new NativeMenuItem("🔄 Rebalance Grid");
        rebalanceItem.Click += (_, _) => OnRebalanceKeyPressed();
        menu.Add(rebalanceItem);

        menu.Add(new NativeMenuItemSeparator());

        // Tiled windows — rename entries (prune dead windows first)
        _tiling?.PruneDeadWindows();
        var tiledSnapshots = _tiling?.GetAllSnapshots() ?? [];
        if (tiledSnapshots.Count > 0)
        {
            foreach (var grid in tiledSnapshots)
            {
                var header = new NativeMenuItem($"Screen {grid.DeviceName}") { IsEnabled = false };
                menu.Add(header);
                foreach (var (slot, ts) in grid.Slots.OrderBy(kv => kv.Key))
                {
                    var title = TilingCoordinator.GetWindowTitle(ts.Hwnd);
                    var displayName = ts.Alias
                        ?? (string.IsNullOrWhiteSpace(title) ? $"Window (slot {slot + 1})" : title);
                    if (displayName.Length > 40)
                        displayName = displayName[..37] + "…";
                    var capturedSlot = slot;
                    var capturedMonitor = grid.MonitorHandle;
                    var renameItem = new NativeMenuItem($"✏️ {displayName}");
                    renameItem.Click += (_, _) => ShowRenameDialog(capturedMonitor, capturedSlot, ts.Alias ?? "");
                    menu.Add(renameItem);
                }
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

    /// <summary>Check if a monitor handle is enabled for tiling in config.</summary>
    private bool IsMonitorEnabled(nint hMonitor)
    {
        if (_config?.EnabledMonitors is null || _config.EnabledMonitors.Count == 0)
            return true; // null/empty = all monitors enabled

        var deviceName = MonitorEnumerator.GetDeviceName(hMonitor);
        return deviceName is not null && _config.EnabledMonitors.Contains(deviceName);
    }

    private void OnTileKeyPressed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_overlay is not null && _overlay.IsVisible)
            {
                DismissOverlay();
                return;
            }

            var monitor = TilingCoordinator.GetMonitorAtCursor();
            if (!IsMonitorEnabled(monitor)) return;

            ShowOverlay();
        });
    }

    private void OnRebalanceKeyPressed()
    {
        if (_tiling is null) return;
        var monitor = TilingCoordinator.GetMonitorAtCursor();
        if (!IsMonitorEnabled(monitor)) return;
        _tiling.Rebalance(monitor);
    }

    private void OnOverlayDismiss() =>
        Dispatcher.UIThread.Post(DismissOverlay);

    private void ShowOverlay()
    {
        _currentMonitor = TilingCoordinator.GetMonitorAtCursor();

        // Don't show overlay on disabled monitors
        if (!IsMonitorEnabled(_currentMonitor)) return;

        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        TilingCoordinator.GetMonitorInfo(_currentMonitor, ref info);

        RunTerminalCollection("overlay");

        var gridSize = _tiling?.GetGridSize(_currentMonitor) ?? 1;
        var layout = _config?.GridLayout ?? "2x3";
        _currentPaneBounds = TilingCoordinator.GetPaneBounds(_currentMonitor, gridSize, layout);

        _overlay = new TileOverlay();
        _overlay.SetLayout(layout);
        _overlay.SetMonitorBounds(info.rcWork, info.rcMonitor);
        _overlay.SetGridSize(gridSize);
        var snapshot = _tiling?.GetSnapshot(_currentMonitor) ?? new Dictionary<int, TileSlot>();
        var aliases = snapshot.Where(kv => kv.Value.Alias is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Alias!);
        _overlay.SetOccupiedSlots(new HashSet<int>(snapshot.Keys), aliases);

        _overlay.DismissRequested += () => Dispatcher.UIThread.Post(DismissOverlay);
        _overlay.RenameRequested += slot =>
        {
            var snap = _tiling?.GetSnapshot(_currentMonitor);
            var currentAlias = snap?.TryGetValue(slot, out var ts) == true ? ts.Alias ?? "" : "";
            ShowRenameDialog(_currentMonitor, slot, currentAlias);
        };
        _overlay.Show();
        // Hotkey-triggered: make interactive (accept clicks for rename/dismiss)
        _overlay.MakeInteractive();
        _hotkeys?.SetOverlayActive(true);
    }

    private void DismissOverlay()
    {
        _hotkeys?.SetOverlayActive(false);
        _overlay?.Close();
        _overlay = null;
        _currentPaneBounds = null;
    }

    private void RunTerminalCollection(string trigger)
    {
        try
        {
            var assigned = _tiling?.CollectVisibleTerminals(_config?.EnabledMonitors, trigger) ?? 0;
            Diag($"TERMINAL_COLLECT_APP trigger={trigger} assigned={assigned}");
        }
        catch (Exception ex)
        {
            Diag($"TERMINAL_COLLECT_APP_ERROR trigger={trigger}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ── Diagnostic log ─────────────────────────────────────────────────────
    private static readonly string _diagLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "TinyBoss", "drag_diag.log");

    private static void Diag(string msg)
    {
        try { File.AppendAllText(_diagLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { /* best-effort */ }
    }

    // ── Drag events → overlay highlighting + snap ────────────────────────────

    private nint _dragHwnd;
    private bool _dragOverlayActive;

    private void OnDragStarted(nint hwnd)
    {
        _dragHwnd = hwnd;
        _dragOverlayActive = false;
        _pageMove = null;

        try
        {
            var isTerminal = TerminalDetector.IsTerminalWindow(hwnd);
            Diag($"DRAG_START hwnd=0x{hwnd:X} isTerminal={isTerminal}");

            if (!isTerminal) return;
            _tiling?.OnDragStarted();

            // Prune dead windows so grid size reflects reality
            var pruned = _tiling?.PruneDeadWindows() ?? false;
            var location = _tiling?.FindLocationForHwnd(hwnd);
            var slotBefore = location?.Slot ?? -1;
            Diag($"  pruned={pruned} existingSlot={slotBefore} source={location?.DeviceName ?? ""}");

            var movePageHeld = _hotkeys?.IsMovePageHeld == true;
            if (movePageHeld && location is not null)
            {
                _pageMove = new PageMoveContext(location.DeviceName, location.MonitorHandle, hwnd);
                _dragOverlayActive = true;
                var moveMonitor = TilingCoordinator.GetMonitorAtCursor();
                var moveEnabled = IsMonitorEnabled(moveMonitor);
                Diag($"  pageMove source={location.DeviceName} monitor=0x{moveMonitor:X} enabled={moveEnabled}");
                if (moveEnabled)
                    Dispatcher.UIThread.Post(() => ShowDragOverlay(moveMonitor));
                return;
            }

            // If this window is already tiled, remove from slot data only.
            // Do NOT rebalance here — SetWindowPos during drag kills the move operation.
            // Reflow happens at drag end.
            _tiling?.RemoveWindow(hwnd);
            Diag($"  occupiedAfterRemove source={location?.DeviceName ?? ""}");

            // Always activate drag tracking for terminals — overlay will appear
            // when cursor reaches any enabled monitor (via OnDragMoved).
            var monitor = TilingCoordinator.GetMonitorAtCursor();
            var enabled = IsMonitorEnabled(monitor);
            Diag($"  monitor=0x{monitor:X} enabled={enabled}");

            _dragOverlayActive = true;

            if (enabled)
            {
                Dispatcher.UIThread.Post(() => ShowDragOverlay(monitor));
            }
            // If not enabled, overlay will show when cursor moves to an enabled monitor
        }
        catch (Exception ex)
        {
            Diag($"DRAG_START_ERROR: {ex.Message}\n{ex.StackTrace}");
            _dragOverlayActive = false;
            _dragHwnd = nint.Zero;
            _pageMove = null;
        }
    }

    /// <summary>Shows click-through overlay during drag (non-interactive).</summary>
    private void ShowDragOverlay(nint monitor)
    {
        if (_overlay is not null) { Diag($"SHOW_OVERLAY skip — already exists"); return; }

        _currentMonitor = monitor;
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        TilingCoordinator.GetMonitorInfo(monitor, ref info);

        var activeMonitor = _tiling?.ActiveMonitor ?? nint.Zero;
        var gridSize = _pageMove is not null
            ? (_tiling?.GetGridSize(monitor) ?? 1)
            : (_tiling?.GetGridSizeForNextWindow(monitor) ?? 1);
        var layout = _config?.GridLayout ?? "2x3";
        _currentPaneBounds = TilingCoordinator.GetPaneBounds(monitor, gridSize, layout);

        Diag($"SHOW_OVERLAY mon=0x{monitor:X} activeMon=0x{activeMonitor:X} pageMove={_pageMove is not null} grid={gridSize} occupied={_tiling?.GetOccupiedCount(monitor)}");

        _overlay = new TileOverlay();
        _overlay.SetLayout(layout);
        _overlay.SetMonitorBounds(info.rcWork, info.rcMonitor);
        _overlay.SetGridSize(gridSize);

        var snapshot = _tiling?.GetSnapshot(monitor) ?? new Dictionary<int, TileSlot>();
        var aliases = snapshot.Where(kv => kv.Value.Alias is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Alias!);
        _overlay.SetOccupiedSlots(new HashSet<int>(snapshot.Keys), aliases);

        _overlay.DismissRequested += () => Dispatcher.UIThread.Post(DismissOverlay);
        _overlay.Show();

        // Re-snap existing tiled windows on this monitor (recovery from drift/corruption)
        if (_pageMove is null && _tiling is not null && _tiling.GetOccupiedCount(monitor) > 0)
        {
            try { _tiling.PositionAll(monitor); }
            catch (Exception ex) { Diag($"RESNAP_ERROR: {ex.Message}"); }
        }
    }

    private void OnDragMoved(int screenX, int screenY)
    {
        if (!_dragOverlayActive) return;

        try
        {
            var monitor = TilingCoordinator.GetMonitorAtPoint(screenX, screenY);
            if (monitor == nint.Zero || !IsMonitorEnabled(monitor)) return;

            // No overlay yet (started on disabled monitor) — create it now
            if (_overlay is null)
            {
                Diag($"DRAG_MOVED late_overlay on=0x{monitor:X}");
                Dispatcher.UIThread.Post(() => ShowDragOverlay(monitor));
                return;
            }

            // If dragged to a different enabled monitor, move the overlay
            if (monitor != _currentMonitor)
            {
                Diag($"DRAG_MOVED monitor_change from=0x{_currentMonitor:X} to=0x{monitor:X}");
                Dispatcher.UIThread.Post(() =>
                {
                    DismissOverlay();
                    ShowDragOverlay(monitor);
                });
                return;
            }

            var slot = TilingCoordinator.HitTestSlot(screenX, screenY, _currentPaneBounds);
            Dispatcher.UIThread.Post(() => _overlay?.HighlightZone(slot));
        }
        catch (Exception ex)
        {
            Diag($"DRAG_MOVED_ERROR: {ex.Message}");
        }
    }

    private void OnDragEnded(nint hwnd, int screenX, int screenY)
    {
        var wasActive = _dragOverlayActive;
        var pageMove = _pageMove;
        _dragHwnd = nint.Zero;
        _dragOverlayActive = false;
        _pageMove = null;

        Diag($"DRAG_END hwnd=0x{hwnd:X} pos=({screenX},{screenY}) wasActive={wasActive} pageMove={pageMove is not null} hasOverlay={_overlay is not null} hasBounds={_currentPaneBounds is not null}");

        try
        {
            if (pageMove is not null)
            {
                HandlePageMoveDrop(pageMove, screenX, screenY);
                return;
            }

            if (!wasActive || _overlay is null || _currentPaneBounds is null)
            {
                Diag($"  early exit - rebalance remaining={_tiling?.GetOccupiedCount(_currentMonitor)}");
                _tiling?.RestoreDetachedWindow(hwnd);
                if (_tiling is not null && _currentMonitor != nint.Zero && _tiling.GetOccupiedCount(_currentMonitor) > 0)
                    _tiling.Rebalance(_currentMonitor);
                Dispatcher.UIThread.Post(DismissOverlay);
                return;
            }

            var slot = TilingCoordinator.HitTestSlot(screenX, screenY, _currentPaneBounds);
            Diag($"  hitTest slot={slot} currentMon=0x{_currentMonitor:X} occupied={_tiling?.GetOccupiedCount(_currentMonitor)}");

            if (slot >= 0 && _tiling is not null)
            {
                var slotOccupied = _tiling.IsSlotOccupied(_currentMonitor, slot);
                Diag($"  slotOccupied={slotOccupied}");
                if (slotOccupied)
                {
                    var empty = _tiling.FindNextEmptySlot(_currentMonitor, _tiling.GetGridSizeForNextWindow(_currentMonitor));
                    Diag($"  redirect to empty={empty}");
                    if (empty < 0)
                    {
                        _tiling.RestoreDetachedWindow(hwnd);
                        Dispatcher.UIThread.Post(DismissOverlay);
                        return;
                    }
                    slot = empty;
                }

                string? sessionId = null;
                if (_registry is not null)
                {
                    TilingCoordinator.GetWindowThreadProcessId(hwnd, out int pid);
                    var session = _registry.GetSnapshot().FirstOrDefault(s => s.Process.Id == pid);
                    sessionId = session?.SessionId;
                }

                _tiling.AssignToSlot(slot, hwnd, _currentMonitor, sessionId);
                Diag($"  ASSIGNED slot={slot} mon=0x{_currentMonitor:X} gridSize={_tiling.GetGridSize(_currentMonitor)} total={_tiling.GetOccupiedCount(_currentMonitor)}");
                _tiling.PositionAll(_currentMonitor);

                Dispatcher.UIThread.Post(() => RefreshOverlayThenDismiss(_currentMonitor));
            }
            else
            {
                Diag($"  no slot - rebalance remaining={_tiling?.GetOccupiedCount(_currentMonitor)}");
                _tiling?.RestoreDetachedWindow(hwnd);
                if (_tiling is not null && _currentMonitor != nint.Zero && _tiling.GetOccupiedCount(_currentMonitor) > 0)
                    _tiling.Rebalance(_currentMonitor);
                Dispatcher.UIThread.Post(DismissOverlay);
            }
        }
        catch (Exception ex)
        {
            Diag($"DRAG_END_ERROR: {ex.Message}\n{ex.StackTrace}");
            Dispatcher.UIThread.Post(DismissOverlay);
        }
        finally
        {
            var endMonitor = _currentMonitor != nint.Zero ? _currentMonitor : pageMove?.SourceMonitor ?? nint.Zero;
            _tiling?.OnDragEnded(endMonitor);
        }
    }

    private void HandlePageMoveDrop(PageMoveContext pageMove, int screenX, int screenY)
    {
        if (_tiling is null)
        {
            Dispatcher.UIThread.Post(DismissOverlay);
            return;
        }

        var targetMonitor = TilingCoordinator.GetMonitorAtPoint(screenX, screenY);
        var targetEnabled = targetMonitor != nint.Zero && IsMonitorEnabled(targetMonitor);
        Diag($"  pageMove target=0x{targetMonitor:X} enabled={targetEnabled}");

        if (!targetEnabled)
        {
            _tiling.Rebalance(pageMove.SourceMonitor);
            Dispatcher.UIThread.Post(DismissOverlay);
            return;
        }

        var targetDevice = _tiling.GetDeviceName(targetMonitor);
        if (string.IsNullOrWhiteSpace(targetDevice))
        {
            _tiling.Rebalance(pageMove.SourceMonitor);
            Dispatcher.UIThread.Post(DismissOverlay);
            return;
        }

        if (string.Equals(targetDevice, pageMove.SourceDevice, StringComparison.OrdinalIgnoreCase))
        {
            _tiling.Rebalance(pageMove.SourceMonitor);
            Dispatcher.UIThread.Post(() => RefreshOverlayThenDismiss(targetMonitor));
            return;
        }

        var moved = _tiling.MovePage(pageMove.SourceDevice, targetDevice);
        Diag($"  pageMove complete source={pageMove.SourceDevice} target={targetDevice} moved={moved}");
        Dispatcher.UIThread.Post(() => RefreshOverlayThenDismiss(targetMonitor));
    }

    private void RefreshOverlayThenDismiss(nint monitor)
    {
        if (_tiling is null)
        {
            DismissOverlay();
            return;
        }

        var snapshot = _tiling.GetSnapshot(monitor);
        var gridSize = _tiling.GetGridSize(monitor);
        _overlay?.SetGridSize(gridSize);
        var aliases = snapshot.Where(kv => kv.Value.Alias is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Alias!);
        _overlay?.SetOccupiedSlots(new HashSet<int>(snapshot.Keys), aliases);
        _overlay?.HighlightZone(-1);
        _currentPaneBounds = TilingCoordinator.GetPaneBounds(monitor, gridSize, _config?.GridLayout ?? "2x3");
        Task.Delay(400).ContinueWith(_ => Dispatcher.UIThread.Post(DismissOverlay));
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
        if (_tiling is not null)
            _tiling.Layout = _config.GridLayout ?? "2x3";
        RebuildTrayMenu();
    }

    private void ShowRenameDialog(nint monitorHandle, int slot, string currentAlias)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var dialog = new RenameDialog(currentAlias);
            dialog.Closed += (_, _) =>
            {
                if (dialog.AliasResult is { } alias && _tiling is not null)
                {
                    _tiling.RenameSlot(monitorHandle, slot, alias);
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
