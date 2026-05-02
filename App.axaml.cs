using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Diagnostics;
using TinyBoss.Voice;
using TinyBoss.Core;
using TinyBoss.Platform.Windows;
using static TinyBoss.Platform.Windows.TilingCoordinator;

namespace TinyBoss;

public class App : Application
{
    private TrayIcon? _trayIcon;
    private SessionRegistry? _registry;
    private VoiceController? _voice;
    private TextInjector? _textInjector;
    private TilingCoordinator? _tiling;
    private DragWatcher? _dragWatcher;
    private HotKeyListener? _hotkeys;
    private TinyBossConfig? _config;
    private TileOverlay? _overlay;
    private string? _voiceTargetSessionId;
    private string? _iconPath;
    private bool _shutdownStarted;

    // Tiling state
    private nint _currentMonitor;
    private Dictionary<int, RECT>? _currentPaneBounds;
    private PageMoveContext? _pageMove;
    private TileLocation? _dragSourceLocation;

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
            desktop.ShutdownRequested += (_, _) => ShutdownTinyBoss();

            _config = TinyBossServices.Provider.GetRequiredService<TinyBossConfig>();
            _registry = TinyBossServices.Provider.GetRequiredService<SessionRegistry>();
            _registry.SessionChanged += OnSessionChanged;

            _voice = TinyBossServices.Provider.GetRequiredService<VoiceController>();
            _voice.RecordingStateChanged += OnRecordingStateChanged;
            _voice.StatusMessage += OnVoiceStatusMessage;
            _textInjector = TinyBossServices.Provider.GetRequiredService<TextInjector>();

            _tiling = TinyBossServices.Provider.GetRequiredService<TilingCoordinator>();
            _tiling.Layout = _config.GridLayout ?? "2x3";
            _tiling.SlotsChanged += OnSlotsChanged;
            // WinEvent callbacks need a message pump, so install from the UI thread too.
            _tiling.InstallAliasTitleHook();

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
        _tiling?.PruneDeadWindows();
        var tiledSnapshots = _tiling?.GetAllSnapshots() ?? [];
        var tiledWindowCount = tiledSnapshots.Sum(g => g.Slots.Count);

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

        var tiledSummary = new NativeMenuItem(
            tiledWindowCount == 1
                ? "Tiled Windows: 1 window"
                : $"Tiled Windows: {tiledWindowCount} windows")
        {
            IsEnabled = false,
        };
        menu.Add(tiledSummary);
        menu.Add(BuildTiledWindowsMenu(tiledSnapshots, tiledWindowCount));

        menu.Add(new NativeMenuItemSeparator());

        // Tiling actions
        var tileItem = new NativeMenuItem("📐 Tile Windows");
        tileItem.Click += (_, _) => OnTileKeyPressed();
        menu.Add(tileItem);

        var rebalanceItem = new NativeMenuItem("🔄 Rebalance Grid");
        rebalanceItem.Click += (_, _) => OnRebalanceKeyPressed();
        menu.Add(rebalanceItem);

        menu.Add(new NativeMenuItemSeparator());

        var terminalBossItem = new NativeMenuItem("Launch TerminalBoss");
        terminalBossItem.Click += (_, _) => LaunchTerminalBoss();
        menu.Add(terminalBossItem);

        var resumeHistoryItem = new NativeMenuItem("Resume History...");
        resumeHistoryItem.Click += (_, _) => ShowResumeHistory();
        menu.Add(resumeHistoryItem);

        var settingsItem = new NativeMenuItem("⚙️ Settings");
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Add(settingsItem);

        var quitItem = new NativeMenuItem("Quit TinyBoss");
        quitItem.Click += (_, _) =>
        {
            RequestShutdown();
        };
        menu.Add(quitItem);

        return menu;
    }

    private NativeMenuItem BuildTiledWindowsMenu(IReadOnlyList<MonitorGridSnapshot> tiledSnapshots, int tiledWindowCount)
    {
        var windowsItem = new NativeMenuItem(
            tiledWindowCount == 1
                ? "Window List (1)"
                : $"Window List ({tiledWindowCount})");
        var windowsMenu = new NativeMenu();

        if (tiledWindowCount == 0)
        {
            windowsMenu.Add(new NativeMenuItem("No tiled windows") { IsEnabled = false });
            windowsItem.Menu = windowsMenu;
            return windowsItem;
        }

        foreach (var grid in tiledSnapshots)
        {
            var screenItem = new NativeMenuItem($"{MonitorEnumerator.FormatDisplayName(grid.DeviceName)} ({grid.Slots.Count})");
            var screenMenu = new NativeMenu();

            foreach (var (slot, ts) in grid.Slots.OrderBy(kv => kv.Key))
            {
                var displayName = GetTiledWindowDisplayName(slot, ts);
                var capturedSlot = slot;
                var capturedTile = ts;
                var capturedMonitor = grid.MonitorHandle;

                var windowItem = new NativeMenuItem($"Slot {slot + 1}: {displayName}");
                var windowMenu = new NativeMenu();

                var renameItem = new NativeMenuItem("Rename...");
                renameItem.Click += (_, _) => ShowRenameDialog(capturedMonitor, capturedSlot, capturedTile.Alias ?? "");
                windowMenu.Add(renameItem);

                var bossifyItem = new NativeMenuItem("Bossify");
                bossifyItem.Click += (_, _) => _ = BossifyWindowAsync(capturedMonitor, capturedSlot, capturedTile);
                windowMenu.Add(bossifyItem);

                var bossifyResumeItem = new NativeMenuItem("Bossify + resume");
                bossifyResumeItem.Click += (_, _) => _ = BossifyResumeWindowAsync(capturedMonitor, capturedSlot, capturedTile);
                windowMenu.Add(bossifyResumeItem);

                windowItem.Menu = windowMenu;
                screenMenu.Add(windowItem);
            }

            screenItem.Menu = screenMenu;
            windowsMenu.Add(screenItem);
        }

        windowsItem.Menu = windowsMenu;
        return windowsItem;
    }

    private static string GetTiledWindowDisplayName(int slot, TileSlot ts)
    {
        var title = TilingCoordinator.GetWindowTitle(ts.Hwnd);
        var displayName = ts.Alias
            ?? (string.IsNullOrWhiteSpace(title) ? $"Window (slot {slot + 1})" : title);
        return displayName.Length > 44 ? displayName[..41] + "..." : displayName;
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

    private static void TryMinimizeWindow(nint hwnd)
    {
        if (hwnd == nint.Zero)
            return;

        try { ShowWindow(hwnd, 6); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    // ── Drag events → overlay highlighting + snap ────────────────────────────

    private nint _dragHwnd;
    private bool _dragOverlayActive;

    private void OnDragStarted(nint hwnd)
    {
        _dragHwnd = hwnd;
        _dragOverlayActive = false;
        _pageMove = null;
        _dragSourceLocation = null;

        try
        {
            var isTerminal = TerminalDetector.IsTerminalWindow(hwnd);
            Diag($"DRAG_START hwnd=0x{hwnd:X} isTerminal={isTerminal}");

            if (!isTerminal) return;
            _tiling?.OnDragStarted();

            // Prune dead windows so grid size reflects reality
            var pruned = _tiling?.PruneDeadWindows() ?? false;
            var location = _tiling?.FindLocationForHwnd(hwnd);
            _dragSourceLocation = location;
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
            _dragSourceLocation = null;
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
        var sourceLocation = _dragSourceLocation;
        var handledDrop = false;
        _dragHwnd = nint.Zero;
        _dragOverlayActive = false;
        _pageMove = null;
        _dragSourceLocation = null;

        Diag($"DRAG_END hwnd=0x{hwnd:X} pos=({screenX},{screenY}) wasActive={wasActive} pageMove={pageMove is not null} hasOverlay={_overlay is not null} hasBounds={_currentPaneBounds is not null}");

        try
        {
            if (pageMove is not null)
            {
                HandlePageMoveDrop(pageMove, screenX, screenY);
                handledDrop = true;
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
                string? sessionId = null;
                if (_registry is not null)
                {
                    TilingCoordinator.GetWindowThreadProcessId(hwnd, out int pid);
                    var session = _registry.GetSnapshot().FirstOrDefault(s => s.Process.Id == pid);
                    sessionId = session?.SessionId;
                }

                var slotOccupied = _tiling.IsSlotOccupied(_currentMonitor, slot);
                Diag($"  slotOccupied={slotOccupied}");
                if (slotOccupied)
                {
                    if (sourceLocation is not null &&
                        _tiling.SwapDetachedWindowWithSlot(hwnd, _currentMonitor, slot, sourceLocation, sessionId))
                    {
                        Diag($"  SWAPPED source={sourceLocation.DeviceName}:{sourceLocation.Slot} targetSlot={slot} mon=0x{_currentMonitor:X}");
                        handledDrop = true;
                        Dispatcher.UIThread.Post(() => RefreshOverlayThenDismiss(_currentMonitor));
                        return;
                    }

                    if (sourceLocation is not null)
                    {
                        Diag("  swap failed - restoring detached window");
                        _tiling.RestoreDetachedWindow(hwnd);
                        Dispatcher.UIThread.Post(DismissOverlay);
                        return;
                    }

                    var empty = _tiling.FindNextEmptySlot(_currentMonitor, _tiling.GetGridSizeForNextWindow(_currentMonitor));
                    Diag($"  no source slot - redirect to empty={empty}");
                    if (empty < 0)
                    {
                        _tiling.RestoreDetachedWindow(hwnd);
                        Dispatcher.UIThread.Post(DismissOverlay);
                        return;
                    }
                    slot = empty;
                }

                _tiling.AssignToSlot(slot, hwnd, _currentMonitor, sessionId);
                Diag($"  ASSIGNED slot={slot} mon=0x{_currentMonitor:X} gridSize={_tiling.GetGridSize(_currentMonitor)} total={_tiling.GetOccupiedCount(_currentMonitor)}");
                _tiling.PositionAll(_currentMonitor);
                if (sourceLocation is not null && sourceLocation.MonitorHandle != _currentMonitor)
                    _tiling.Rebalance(sourceLocation.MonitorHandle);
                handledDrop = true;

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
            _tiling?.OnDragEnded(endMonitor, applyPendingReflow: !handledDrop);
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

    private Process? LaunchTerminalBoss(string? name = null, string? cwd = null, string? tint = null, string? run = null)
    {
        try
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("TERMINALBOSS_EXE_PATH"),
                Environment.GetEnvironmentVariable("BOSS" + "TERMINAL_EXE_PATH"),
                Path.Combine(AppContext.BaseDirectory, "TerminalBoss.exe"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TerminalBoss", "bin", "Debug", "net10.0-windows", "TerminalBoss.exe")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TerminalBoss", "TerminalBoss.exe")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TerminalBoss", "bin", "Debug", "net10.0-windows", "TerminalBoss.exe")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TerminalBoss", "TerminalBoss.exe")),
            };

            var exe = candidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .FirstOrDefault(File.Exists);

            if (exe is null)
            {
                Diag("TERMINALBOSS_LAUNCH missing executable");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            };
            if (!string.IsNullOrWhiteSpace(name))
            {
                psi.ArgumentList.Add("--name");
                psi.ArgumentList.Add(name);
            }
            if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            {
                psi.ArgumentList.Add("--cwd");
                psi.ArgumentList.Add(cwd);
            }
            if (!string.IsNullOrWhiteSpace(tint))
            {
                psi.ArgumentList.Add("--tint");
                psi.ArgumentList.Add(tint);
            }
            if (!string.IsNullOrWhiteSpace(run))
            {
                psi.ArgumentList.Add("--run");
                psi.ArgumentList.Add(run);
            }

            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Diag($"TERMINALBOSS_LAUNCH_ERROR: {ex.Message}");
            return null;
        }
    }

    private async Task BossifyWindowAsync(nint monitorHandle, int slot, TileSlot tile)
    {
        var title = TilingCoordinator.GetWindowTitle(tile.Hwnd);
        var name = tile.Alias
            ?? (string.IsNullOrWhiteSpace(title) ? $"Window {slot + 1}" : title);
        var cwd = ResolveManagedCwd(tile);

        var process = LaunchTerminalBoss(name, cwd);
        if (!await PlaceTerminalBossReplacementAsync(process, tile.Hwnd, monitorHandle, slot, name, "bossify"))
            RefreshBossifyFallback(monitorHandle, "bossify-fallback");
    }

    private async Task BossifyResumeWindowAsync(nint monitorHandle, int slot, TileSlot tile)
    {
        var title = TilingCoordinator.GetWindowTitle(tile.Hwnd);
        var name = tile.Alias
            ?? (string.IsNullOrWhiteSpace(title) ? $"Window {slot + 1}" : title);
        var cwd = ResolveManagedCwd(tile);

        if (_textInjector is null)
        {
            var process = LaunchTerminalBoss(name, cwd);
            await PlaceTerminalBossReplacementAsync(process, tile.Hwnd, monitorHandle, slot, name, "bossify-resume-no-injector");
            return;
        }

        try
        {
            var exitResult = await _textInjector.InjectWindowAsync("/exit", tile.Hwnd, CancellationToken.None);
            if (!exitResult.Success)
            {
                Diag($"BOSSIFY_RESUME_EXIT_INJECT_FAILED hwnd=0x{tile.Hwnd:X}: {exitResult.Message}");
                return;
            }

            var resumeCommand = await WaitForResumeCommandAsync(tile.Hwnd);
            if (string.IsNullOrWhiteSpace(resumeCommand))
            {
                Diag($"BOSSIFY_RESUME_COMMAND_NOT_FOUND hwnd=0x{tile.Hwnd:X} name={name}");
                return;
            }

            ResumeSessionHistory.Append(new ResumeSessionHistoryRecord(
                CapturedAt: DateTimeOffset.UtcNow,
                Name: name,
                Cwd: cwd,
                Command: resumeCommand,
                Tool: ResumeSessionHistory.InferTool(resumeCommand),
                Source: "bossify-resume",
                Slot: slot,
                MonitorHandle: monitorHandle.ToString(),
                OldHwnd: tile.Hwnd.ToString()));

            var process = LaunchTerminalBoss(name, cwd, run: resumeCommand);
            if (!await PlaceTerminalBossReplacementAsync(process, tile.Hwnd, monitorHandle, slot, name, "bossify-resume"))
                RefreshBossifyFallback(monitorHandle, "bossify-resume-fallback");
        }
        catch (Exception ex)
        {
            Diag($"BOSSIFY_RESUME_ERROR hwnd=0x{tile.Hwnd:X}: {ex.Message}");
            var process = LaunchTerminalBoss(name, cwd);
            if (!await PlaceTerminalBossReplacementAsync(process, tile.Hwnd, monitorHandle, slot, name, "bossify-resume-error-fallback"))
                RefreshBossifyFallback(monitorHandle, "bossify-resume-error-collect");
        }
    }

    private async Task<bool> PlaceTerminalBossReplacementAsync(
        Process? process,
        nint oldHwnd,
        nint monitorHandle,
        int slot,
        string name,
        string source)
    {
        if (process is null || _tiling is null)
            return false;

        var newHwnd = await WaitForMainWindowHandleAsync(process);
        if (newHwnd == nint.Zero || !TilingCoordinator.IsWindow(newHwnd))
        {
            Diag($"BOSSIFY_REPLACE_NO_HWND source={source} pid={process.Id}");
            return false;
        }

        _tiling.RememberWindowAlias(newHwnd, name, applyNow: true);
        var assigned = _tiling.AssignToSlot(slot, newHwnd, monitorHandle);
        if (!assigned)
        {
            Diag($"BOSSIFY_REPLACE_ASSIGN_FAILED source={source} pid={process.Id} hwnd=0x{newHwnd:X} slot={slot} monitor=0x{monitorHandle:X}");
            return false;
        }

        TryMinimizeWindow(oldHwnd);
        if (monitorHandle != nint.Zero)
            RefreshOverlayAliases(monitorHandle);
        RebuildTrayMenu();
        Diag($"BOSSIFY_REPLACED source={source} old=0x{oldHwnd:X} new=0x{newHwnd:X} slot={slot} pid={process.Id}");
        return true;
    }

    private static async Task<nint> WaitForMainWindowHandleAsync(Process process)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (process.HasExited)
                    return nint.Zero;

                process.Refresh();
                if (process.MainWindowHandle != nint.Zero)
                    return process.MainWindowHandle;
            }
            catch
            {
                return nint.Zero;
            }

            await Task.Delay(100);
        }

        return nint.Zero;
    }

    private void RefreshBossifyFallback(nint monitorHandle, string trigger)
    {
        _tiling?.CollectVisibleTerminals(_config?.EnabledMonitors, trigger);
        if (monitorHandle != nint.Zero)
            RefreshOverlayAliases(monitorHandle);
        RebuildTrayMenu();
    }

    private static async Task<string?> WaitForResumeCommandAsync(nint hwnd)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var command = ResumeCommandExtractor.Find(WindowTextCapture.CaptureTail(hwnd));
            if (!string.IsNullOrWhiteSpace(command))
                return command;

            await Task.Delay(350);
        }

        return null;
    }

    private string? ResolveManagedCwd(TileSlot tile)
    {
        if (_registry is not null)
        {
            var sessions = _registry.GetSnapshot();
            var bySessionId = string.IsNullOrWhiteSpace(tile.SessionId)
                ? null
                : sessions.FirstOrDefault(s =>
                    s.SessionId.Equals(tile.SessionId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(bySessionId?.Cwd))
                return bySessionId.Cwd;

            var byPid = sessions.FirstOrDefault(s => s.Process.Id == tile.ProcessId);
            if (!string.IsNullOrWhiteSpace(byPid?.Cwd))
                return byPid.Cwd;
        }

        var externalCwd = ProcessWorkingDirectory.TryGet(tile.ProcessId);
        return string.IsNullOrWhiteSpace(externalCwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : externalCwd;
    }

    private void ShowResumeHistory()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var window = new ResumeHistoryWindow();
            window.Show();
            window.Activate();
        });
    }

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
        {
            _tiling.Layout = _config.GridLayout ?? "2x3";
            _tiling.CollectVisibleTerminals(_config.EnabledMonitors, "settings");
        }
        RebuildTrayMenu();
    }

    private void ShowRenameDialog(nint monitorHandle, int slot, string currentAlias)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var dialog = new RenameDialog(currentAlias);
            dialog.Closed += (_, _) =>
            {
                if (dialog.Accepted && dialog.AliasResult is { } alias && _tiling is not null)
                {
                    _tiling.RenameSlot(monitorHandle, slot, alias);
                    RefreshOverlayAliases(monitorHandle);
                    RebuildTrayMenu();
                }
            };
            dialog.Show();
            dialog.Activate();
            dialog.Topmost = true;
            await Task.Delay(100);
            dialog.Topmost = false;
        });
    }

    private void RefreshOverlayAliases(nint monitorHandle)
    {
        if (_overlay is null || _tiling is null)
            return;

        var snapshot = _tiling.GetSnapshot(monitorHandle);
        var aliases = snapshot.Where(kv => kv.Value.Alias is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Alias!);
        _overlay.SetOccupiedSlots(new HashSet<int>(snapshot.Keys), aliases);
    }

    private void CheckFirstRun()
    {
        if (_config?.IsFirstRun == true)
        {
            ShowSettings();
            _config.Save(); // Creates the file so next launch isn't first-run
        }
    }

    private void RequestShutdown()
    {
        ShutdownTinyBoss();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Dispatcher.UIThread.Post(() => desktop.TryShutdown(0));
    }

    private void ShutdownTinyBoss()
    {
        if (_shutdownStarted)
            return;
        _shutdownStarted = true;

        try { _registry!.SessionChanged -= OnSessionChanged; } catch { }
        try { _voice!.RecordingStateChanged -= OnRecordingStateChanged; } catch { }
        try { _voice!.StatusMessage -= OnVoiceStatusMessage; } catch { }
        try { _tiling!.SlotsChanged -= OnSlotsChanged; } catch { }
        try { _dragWatcher!.DragStarted -= OnDragStarted; } catch { }
        try { _dragWatcher!.DragMoved -= OnDragMoved; } catch { }
        try { _dragWatcher!.DragEnded -= OnDragEnded; } catch { }
        try { _hotkeys!.TileKeyPressed -= OnTileKeyPressed; } catch { }
        try { _hotkeys!.RebalanceKeyPressed -= OnRebalanceKeyPressed; } catch { }
        try { _hotkeys!.OverlayDismiss -= OnOverlayDismiss; } catch { }

        try { _hotkeys?.SetOverlayActive(false); } catch { }
        try { _overlay?.Close(); } catch { }
        _overlay = null;
        _currentPaneBounds = null;

        try { _settingsWindow?.Close(); } catch { }
        _settingsWindow = null;

        try { _voice?.Dispose(); } catch { }
        _voice = null;
        try { _dragWatcher?.Dispose(); } catch { }
        _dragWatcher = null;
        try { _hotkeys?.Dispose(); } catch { }
        _hotkeys = null;
        try { _tiling?.Dispose(); } catch { }
        _tiling = null;

        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;
                (_trayIcon as IDisposable)?.Dispose();
                _trayIcon = null;
            }
        }
        catch { }
    }
}
