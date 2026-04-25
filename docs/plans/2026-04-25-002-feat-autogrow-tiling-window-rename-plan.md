---
title: "feat: Auto-grow terminal tiling + window rename"
type: feat
status: active
date: 2026-04-25
origin: docs/brainstorms/2026-04-25-window-rename-brainstorm.md
deepened: 2026-04-25
---

# feat: Auto-grow Terminal Tiling + Window Rename

## Enhancement Summary

**Deepened on:** 2026-04-25
**Research agents:** 5 (win32-terminal-detect, autotile-patterns, setwindowtext-durability, avalonia-dialog, pipe-protocol)
**Sources studied:** Komorebi (14.4k★ Rust tiling WM), FancyZones (PowerToys), Avalonia 11 docs, KH codebase conventions

### Key Improvements
1. **Debounce reduced 300ms → 100ms** — research shows <100ms feels instantaneous; 300ms has noticeable lag
2. **Pre-validate HWNDs before BeginDeferWindowPos** — windows can close mid-batch, invalidating hdwp handle (crash vector)
3. **Pause reflow during drag** — prevents window snap-back jitter (Komorebi pattern)
4. **Terminal class names confirmed** — `CASCADIA_HOSTING_WINDOW_CLASS` (all WT variants), `ConsoleWindowClass` (cmd/PS), `mintty` (Git Bash)
5. **VS Code terminal NOT detectable** — embedded XTermjs shares Electron HWND (`Chrome_WidgetWin_1`), cannot be separated
6. **SetWindowText one-shot only** — terminals overwrite within 0-5ms via SetConsoleTitle; EVENT_OBJECT_NAMECHANGE hooks cause feedback loops; PowerToys doesn't touch titles either
7. **Avalonia 11 has no InputDialog** — use separate Window with Show()+Closed, OnKeyDown for Enter/Escape
8. **Pipe protocol matches KH conventions exactly** — KhEnvelope pattern, sealed record payloads, Ack/Error helpers

### New Considerations Discovered
- Windows Terminal tabs: **one HWND per window** (not per tab) — tabs are internal rendering
- Tool window filtering: check `GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW` to exclude popup/tooltip HWNDs
- Fullscreen layout: use `ShowWindow(SW_MAXIMIZE)` + `SWP_NOACTIVATE` (hybrid approach avoids focus theft)
- Multi-monitor: per-monitor grid states possible but deferred (current single-monitor approach is fine for v1)

## Overview

Two tightly coupled features for TinyBoss's tiling system:

1. **Auto-grow/shrink grid** — Grid size adapts automatically as terminal windows are added/removed. No manual grid selection.
2. **Window rename** — Three entry points (tray menu, overlay right-click, named pipe API) to alias a tiled window with both `SetWindowText` and an internal alias.

(see brainstorm: `docs/brainstorms/2026-04-25-window-rename-brainstorm.md`)

## Problem Statement

Currently the user must manually pick a grid size (2/4/6) and drag windows to specific zones. This is friction-heavy for the common case: "I just want my terminals arranged." The user wants to drop terminals and have the grid figure itself out.

Additionally, there's no way to label/rename tiled windows, making it hard to identify them in the tray menu or overlay — especially when all are "Windows Terminal".

## Proposed Solution

### Auto-grow/shrink

Remove fixed grid selection. The grid size is derived from the count of tiled windows:

| Count | Layout | Notes |
|-------|--------|-------|
| 1 | Fullscreen | Single pane |
| 2 | 2-pane (side-by-side) | Left/Right |
| 3–4 | 2×2 | 3 windows = one empty slot |
| 5–6 | 2×3 | 5 windows = one empty slot |
| 7+ | **Rejected** | Toast notification: "Grid full — close a terminal first" |

**Terminal-only filtering**: Only tile windows matching:
- Window class: `CASCADIA_HOSTING_WINDOW_CLASS` (Windows Terminal — all variants including Preview/Canary), `ConsoleWindowClass` (cmd/conhost/PowerShell), `mintty` (Git Bash)
- Process name fallback: `WindowsTerminal.exe`, `cmd.exe`, `powershell.exe`, `pwsh.exe`, `mintty.exe`
- Pre-filter: `IsWindowVisible()` + reject tool windows (`WS_EX_TOOLWINDOW` flag)
- Other windows are silently ignored when dropped on the overlay.
- **Note**: VS Code integrated terminal shares Electron's `Chrome_WidgetWin_1` class — cannot be detected separately via Win32. Only standalone terminal windows are supported.
- **Note**: Windows Terminal uses one HWND per window (tabs are internal rendering, not separate HWNDs).

**Reflow rules**:
- When grid resizes, existing windows preserve spatial order (top-left stays top-left) — insertion-order compaction (most predictable per Komorebi research)
- New window fills the first empty slot (reading order: left→right, top→bottom)
- Reflow is debounced (**100ms**) — research shows <100ms feels instantaneous; 300ms has noticeable lag
- Defer reflow while a drag is in progress — queue and execute after drop (prevents snap-back jitter)

**Shrink threshold**: Grid only shrinks when all remaining windows fit in the smaller layout. E.g., 3 windows in a 2×2 grid do NOT shrink to 2-pane — they stay 2×2 with one empty slot. Only when count ≤ 2 does it shrink to 2-pane.

**Multi-monitor**: Each monitor gets its own independent grid (up to 6 windows each). Monitor identity persisted via `MONITORINFOEX.szDevice` (stable across reboots, unlike HMONITOR handles). All monitors enabled by default — user can disable specific monitors in settings.

| Detail | Approach |
|--------|----------|
| Identity | `MONITORINFOEX.szDevice` (e.g., `\\.\DISPLAY1`) — stable across sessions |
| Grid state | `Dictionary<string, MonitorGridState>` keyed by device name |
| HMONITOR lookup | Resolve device name → HMONITOR at startup + on `WM_DISPLAYCHANGE` |
| Window assignment | Drop target determines which monitor's grid receives the window |
| Overlay | Shown on the monitor where the hotkey is pressed (foreground window's monitor) |
| Settings | List of enabled monitor device names in `TinyBossConfig.EnabledMonitors` (null = all) |

### Window Rename

Three entry points, one outcome:

1. **Tray menu** → per-slot "✏️ Rename" item → opens small Avalonia dialog
2. **Tile overlay** → right-click occupied zone → same dialog
3. **Named pipe API** → `{ "action": "rename", "slot": 0, "alias": "DRM Scheduler" }`

Rename does:
- Calls `SetWindowText(hwnd, alias)` — best-effort, terminals may overwrite
- Stores alias in `TileSlot.Alias` — TinyBoss UI (tray menu, overlay labels) always uses alias when set
- Alias survives grid reflow (it follows the window, not the slot position)

**Title-bar durability**: Terminals constantly reset their title. We call `SetWindowText` once as a courtesy but the alias in TinyBoss's own UI is the reliable display. Future: can add `EVENT_OBJECT_NAMECHANGE` hook to re-apply.

## Technical Approach

### Phase 1: Auto-grow/shrink Grid

#### 1.1 Terminal detection utility

**New file**: `Platform/Windows/TerminalDetector.cs`

```csharp
[SupportedOSPlatform("windows")]
public static class TerminalDetector
{
    private static readonly HashSet<string> TerminalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASCADIA_HOSTING_WINDOW_CLASS",  // Windows Terminal (all variants)
        "ConsoleWindowClass",             // cmd, PowerShell 5.1/7, conhost
        "mintty",                         // Git Bash
    };
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "cmd", "powershell", "pwsh", "mintty",
    };

    public static bool IsTerminalWindow(nint hwnd)
    {
        // 1. Must be visible, non-tool window
        if (!IsWindowVisible(hwnd)) return false;
        if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;

        // 2. Primary: check window class name
        var sb = new StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) > 0)
        {
            if (TerminalClasses.Contains(sb.ToString())) return true;
        }

        // 3. Fallback: check process name
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            var proc = Process.GetProcessById((int)pid);
            return TerminalProcesses.Contains(proc.ProcessName);
        }
        catch { return false; }
    }
}
```

P/Invoke needed:
```csharp
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

[DllImport("user32.dll")]
public static extern bool IsWindowVisible(nint hWnd);

[DllImport("user32.dll")]
public static extern int GetWindowLong(nint hWnd, int nIndex);

private const int GWL_EXSTYLE = -20;
private const int WS_EX_TOOLWINDOW = 0x00000080;
```

### Research Insights: Terminal Detection

**Class name coverage:**
| Terminal | Class Name | Notes |
|----------|-----------|-------|
| Windows Terminal (all) | `CASCADIA_HOSTING_WINDOW_CLASS` | Preview & Canary use same class |
| cmd.exe / conhost | `ConsoleWindowClass` | Legacy console |
| PowerShell 7 (pwsh) | `ConsoleWindowClass` | Console mode |
| PowerShell 5.1 | `ConsoleWindowClass` | Legacy |
| Git Bash (mintty) | `mintty` | Primary class |
| Alacritty | `alacritty` | Can add later |
| WezTerm | `wezterm-gui` | Can add later |

**Architecture note**: Windows Terminal tabs are internal rendering — one HWND per window, not per tab. Tiling manages whole WT windows.

**VS Code caveat**: Integrated terminal shares Electron's `Chrome_WidgetWin_1` class. Cannot be detected separately. Only standalone terminal windows are supported.

#### 1.2 Derive grid size from count

**File**: `Platform/Windows/TilingCoordinator.cs`

Replace manual `GridSize` property with a computed one:

```csharp
public int GridSize => OccupiedCount switch
{
    0 or 1 => 1,   // fullscreen (new layout)
    2 => 2,
    3 or 4 => 4,
    _ => 6,
};
```

Add `case 1` to `GetPaneBounds`:
```csharp
case 1: // fullscreen
    result[0] = new RECT(wa.Left, wa.Top, wa.Right, wa.Bottom);
    break;
```

Remove `_gridSize` field. Remove public `GridSize` setter. Remove `NormalizeGridSize` usage for the setter.

Update `Rebalance` to use the new computed `GridSize` after compacting.

#### 1.3 Auto-reflow on add/remove

**File**: `Platform/Windows/TilingCoordinator.cs`

- `AssignToSlot` → after adding, call `ReflowIfNeeded(monitorHandle)`
- `RemoveWindow` / `RemoveSlotLocked` → after removing, call `ReflowIfNeeded(monitorHandle)`
- New method `ReflowIfNeeded` compacts slots + positions all windows

Add reflow debounce:
```csharp
private Timer? _reflowDebounce;
private const int REFLOW_DEBOUNCE_MS = 100; // Research: <100ms feels instantaneous

private void ScheduleReflow(string monitorDevice)
{
    _reflowDebounce?.Dispose();
    _reflowDebounce = new Timer(_ =>
    {
        Rebalance(monitorDevice);
    }, null, REFLOW_DEBOUNCE_MS, Timeout.Infinite);
}
```

**Drag-aware reflow** (prevents snap-back jitter):
```csharp
private bool _dragInProgress;

public void OnDragStarted() { _dragInProgress = true; }
public void OnDragEnded(string monitorDevice)
{
    _dragInProgress = false;
    if (_reflowPending) ScheduleReflow(monitorDevice);
}
```

#### 1.4 Per-monitor grid state

**File**: `Platform/Windows/TilingCoordinator.cs`

Replace single `_slots` dictionary with per-monitor state:

```csharp
public sealed class MonitorGridState
{
    public string DeviceName { get; init; } = "";
    public nint MonitorHandle { get; set; }
    public Dictionary<int, TileSlot> Slots { get; } = new();
    public int OccupiedCount => Slots.Count;
    public int GridSize => OccupiedCount switch
    {
        0 or 1 => 1,
        2 => 2,
        3 or 4 => 4,
        _ => 6,
    };
}

private readonly Dictionary<string, MonitorGridState> _monitorGrids = new();
```

**Monitor identity**: Use `MONITORINFOEX` to get stable `szDevice` name:
```csharp
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct MONITORINFOEX
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}
```

**Resolve HMONITOR ↔ device name** at startup and on `WM_DISPLAYCHANGE`:
```csharp
private void RefreshMonitorMap()
{
    EnumDisplayMonitors(nint.Zero, nint.Zero, (hMon, _, _, _) =>
    {
        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMon, ref info);
        if (_monitorGrids.TryGetValue(info.szDevice, out var grid))
            grid.MonitorHandle = hMon;
        else
            _monitorGrids[info.szDevice] = new MonitorGridState
            {
                DeviceName = info.szDevice,
                MonitorHandle = hMon,
            };
        return true;
    }, nint.Zero);
}
```

#### 1.5 Pre-validate before BeginDeferWindowPos

**File**: `Platform/Windows/TilingCoordinator.cs`

Critical safety: windows can close between Begin and End, invalidating the hdwp handle.

```csharp
private void PositionAllLocked(MonitorGridState grid)
{
    var bounds = GetPaneBounds(grid.MonitorHandle, grid.GridSize);

    // Pre-validate — remove dead windows before batching
    var deadSlots = grid.Slots
        .Where(kv => !IsWindow(kv.Value.Hwnd))
        .Select(kv => kv.Key).ToList();
    foreach (var s in deadSlots) grid.Slots.Remove(s);

    var validSlots = grid.Slots
        .Where(kv => bounds.ContainsKey(kv.Key))
        .ToList();

    var hdwp = BeginDeferWindowPos(validSlots.Count);
    if (hdwp == nint.Zero) return;

    foreach (var (slot, ts) in validSlots)
    {
        var r = bounds[slot];
        hdwp = DeferWindowPos(hdwp, ts.Hwnd, nint.Zero,
            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (hdwp == nint.Zero) return; // fallback to individual SetWindowPos
    }
    EndDeferWindowPos(hdwp);
}
```

#### 1.6 Auto-assign next empty slot

**File**: `Platform/Windows/TilingCoordinator.cs`

New method (per-monitor):
```csharp
public int FindNextEmptySlot(string monitorDevice)
{
    lock (_lock)
    {
        if (!_monitorGrids.TryGetValue(monitorDevice, out var grid)) return 0;
        int max = grid.GridSize;
        for (int i = 0; i < max; i++)
            if (!grid.Slots.ContainsKey(i)) return i;
        return -1; // full (6 windows on this monitor)
    }
}
```

#### 1.7 Reject at capacity

**File**: `App.axaml.cs` — `OnDragEnded`

Before assigning (per-monitor cap of 6):
```csharp
var monitorDevice = _tiling.GetMonitorDeviceForHwnd(hwnd);
if (_tiling.GetOccupiedCount(monitorDevice) >= 6)
{
    // Show toast / ignore
    return;
}
if (!TerminalDetector.IsTerminalWindow(hwnd))
    return;
```

#### 1.8 Update overlay + tray menu

- Remove grid cycle (Tab) from overlay — grid is automatic now
- Remove `CycleGridSize()` from `TileOverlay.axaml.cs`
- Remove `OverlayCycleLayout` event handling from `App.axaml.cs`
- Remove grid size from tray menu label (it's automatic)
- Update `ShowOverlay()` to use computed `GridSize`

#### 1.9 Update settings

**File**: `Core/TinyBossConfig.cs`

Remove `GridSize` setting and `NormalizedGridSize` (no longer user-configurable).
Add monitor selection:

```csharp
/// <summary>
/// List of monitor device names where tiling is enabled.
/// Null or empty = all monitors enabled.
/// Example: ["\\.\DISPLAY1", "\\.\DISPLAY3"]
/// </summary>
public List<string>? EnabledMonitors { get; set; }
```

Remove grid-related UI from `SettingsWindow.axaml` if present.
Add monitor picker to settings (list checkboxes for each connected monitor with device name + resolution).

#### 1.10 Hotkey picker in settings

**File**: `SettingsWindow.axaml` + `SettingsWindow.axaml.cs`

Config already stores `TileModifiers`/`TileKey`, `RebalanceModifiers`/`RebalanceKey`, and `VoiceModifiers`/`VoiceKey` — but there's no UI to change tiling hotkeys. Add a hotkey section with preset dropdowns:

**Tile overlay hotkey presets:**

| Label | Modifiers | Key | Code |
|-------|-----------|-----|------|
| Ctrl+Shift+G (default) | `0x0006` | `0x47` | MOD_CONTROL\|MOD_SHIFT + G |
| Shift+\` (backtick) | `0x0004` | `0xC0` | MOD_SHIFT + VK_OEM_3 |
| Ctrl+Shift+Space | `0x0006` | `0x20` | MOD_CONTROL\|MOD_SHIFT + VK_SPACE |
| Win+\` | `0x0008` | `0xC0` | MOD_WIN + VK_OEM_3 |

**Implementation**:
```csharp
public sealed record HotkeyPreset(string Label, int Modifiers, int Key);

private static readonly HotkeyPreset[] TilePresets = new[]
{
    new HotkeyPreset("Ctrl+Shift+G", 0x0006, 0x47),
    new HotkeyPreset("Shift+`",      0x0004, 0xC0),
    new HotkeyPreset("Ctrl+Shift+Space", 0x0006, 0x20),
    new HotkeyPreset("Win+`",        0x0008, 0xC0),
};
```

**UX**: ComboBox with preset labels. On change → update config → show "Restart to apply" badge (hotkeys registered once in HotKeyListener constructor). Future: live re-register without restart.

### Phase 2: Window Rename

#### 2.1 Add Alias to TileSlot

**File**: `Platform/Windows/TilingCoordinator.cs:13`

```csharp
public sealed record TileSlot(nint Hwnd, int ProcessId, string? SessionId, string? Alias = null);
```

#### 2.2 RenameSlot method + SetWindowText P/Invoke

**File**: `Platform/Windows/TilingCoordinator.cs`

```csharp
public bool RenameSlot(int slot, string alias, string monitorDevice)
{
    lock (_lock)
    {
        if (!_monitorGrids.TryGetValue(monitorDevice, out var grid)) return false;
        if (!grid.Slots.TryGetValue(slot, out var ts)) return false;
        if (!IsWindow(ts.Hwnd)) { grid.Slots.Remove(slot); return false; }
        grid.Slots[slot] = ts with { Alias = alias };
        SetWindowText(ts.Hwnd, alias); // One-shot courtesy — terminals overwrite within 0-5ms
    }
    SlotsChanged?.Invoke();
    return true;
}

[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern bool SetWindowText(nint hWnd, string lpString);
```

### Research Insights: SetWindowText Durability

- **Terminals overwrite within 0-5ms** — console title owned by `SetConsoleTitle()` (console buffer), not `SetWindowText()` (HWND)
- **EVENT_OBJECT_NAMECHANGE hooks cause feedback loops** — don't try to re-apply
- **PowerToys FancyZones doesn't touch titles at all** — validates our alias-first approach
- **Decision**: Alias is source of truth for TinyBoss UI. `SetWindowText` is a one-shot courtesy. Don't fight it.

#### 2.3 Rename dialog

**New files**: `RenameDialog.axaml` + `RenameDialog.axaml.cs`

Small modal: TextBox pre-filled with current alias (or raw window title), OK + Cancel buttons. Pattern follows `SettingsWindow.axaml`.

```xml
<Window Title="Rename Window" Width="350" Height="160"
        CanResize="False" ShowInTaskbar="False"
        WindowStartupLocation="CenterScreen"
        Icon="/Assets/TinyBoss.ico">
  <StackPanel Margin="24" Spacing="12">
    <TextBlock Text="Window alias:" FontWeight="SemiBold" />
    <TextBox x:Name="AliasInput" />
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Spacing="8">
      <Button x:Name="OkButton" Content="OK" Width="80" />
      <Button x:Name="CancelButton" Content="Cancel" Width="80" />
    </StackPanel>
  </StackPanel>
</Window>
```

Code-behind exposes `string? AliasResult` — set on OK, null on Cancel.

### Research Insights: Avalonia Dialog

- **Avalonia 11 has no built-in InputDialog** — use separate Window class
- **Show() + Closed event** (not ShowDialog) matches existing SettingsWindow pattern
- **Enter/Escape**: Override `OnKeyDown` — Enter = OK, Escape = Cancel
- **Pre-fill TextBox** with current alias and call `SelectAll()` for quick replacement
- **From pipe handler**: Use `Dispatcher.UIThread.Post()` to marshal to UI thread

#### 2.4 Tray menu rename items

**File**: `App.axaml.cs` — `BuildTrayMenu()`

After existing session items, add a "Tiled Windows" section:

```csharp
var snapshot = _tiling?.GetSnapshot() ?? new();
if (snapshot.Count > 0)
{
    menu.Add(new NativeMenuItemSeparator());
    foreach (var (slot, ts) in snapshot.OrderBy(kv => kv.Key))
    {
        var label = ts.Alias ?? $"Slot {slot + 1} (PID {ts.ProcessId})";
        var renameItem = new NativeMenuItem($"✏️ {label}");
        var s = slot;
        renameItem.Click += (_, _) => ShowRenameDialog(s, ts.Alias ?? "");
        menu.Add(renameItem);
    }
}
```

#### 2.5 Overlay right-click → rename

**File**: `Platform/Windows/TileOverlay.axaml.cs`

Add event: `public event Action<int>? RenameRequested;`

Update `PointerPressed`:
```csharp
PointerPressed += (_, e) =>
{
    var pt = e.GetCurrentPoint(this);
    if (pt.Properties.IsLeftButtonPressed)
        DismissRequested?.Invoke();
    else if (pt.Properties.IsRightButtonPressed)
    {
        var pos = e.GetPosition(_canvas);
        var slot = HitTestLocal(pos);
        if (slot >= 0 && _occupiedSlots.Contains(slot))
            RenameRequested?.Invoke(slot);
    }
};
```

Add `HitTestLocal(Point pos)` that converts Canvas-local coords to slot index.

**File**: `App.axaml.cs` — `ShowOverlay()` — wire event:
```csharp
_overlay.RenameRequested += slot => Dispatcher.UIThread.Post(() => ShowRenameDialog(slot, ...));
```

#### 2.6 Named pipe rename handler

**New file**: `Handlers/RenameHandler.cs`

Pattern follows `InjectHandler.cs`:
```csharp
public sealed class RenameHandler
{
    private readonly TilingCoordinator _tiling;
    public async Task HandleAsync(KhEnvelope envelope, Func<KhEnvelope, Task> send, CancellationToken ct)
    {
        var payload = envelope.Payload.Deserialize<RenamePayload>();
        if (payload is null) { await send(ErrorEnvelope("invalid_payload")); return; }
        var ok = _tiling.RenameSlot(payload.Slot, payload.Alias);
        await send(ok ? AckEnvelope(payload.Slot, payload.Alias) : ErrorEnvelope("slot_empty"));
    }
}
```

**File**: `Protocol/TinyBossEnvelope.cs` — add:
```csharp
public const string Rename = "rename";
public sealed record RenamePayload(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("monitor")] string? Monitor = null); // null = primary monitor
```

**File**: `Protocol/MessageHandler.cs` — inject `RenameHandler`, add dispatch case.

**Response contract**:
```json
// Success
{ "action": "ack", "ok": true, "slot": 0, "alias": "DRM Scheduler" }
// Error
{ "action": "error", "ok": false, "error": "slot_empty" }
```

### Research Insights: Pipe Protocol

- **Matches KH conventions exactly**: KhEnvelope with Type/SessionId/Payload, KhMessageType string constants
- **Sealed record payloads** with `[JsonPropertyName("snake_case")]`
- **HandleAsync signature**: `(KhEnvelope, Func<KhEnvelope, Task>, CancellationToken)`
- **Ack/Error helpers**: `AckPayload(bool ok, string? message)`
- **DI registration**: Singleton in `Program.cs`, dispatched via `MessageHandler` switch on `envelope.Type`

#### 2.7 ShowRenameDialog in App.axaml.cs

```csharp
private void ShowRenameDialog(int slot, string currentAlias)
{
    var dialog = new RenameDialog(currentAlias);
    dialog.Closed += (_, _) =>
    {
        if (dialog.AliasResult is not null && _tiling is not null)
        {
            if (_tiling.RenameSlot(slot, dialog.AliasResult))
                RebuildTrayMenu();
        }
    };
    dialog.Show();
}
```

## System-Wide Impact

- **Interaction graph**: `AssignToSlot` → `ScheduleReflow` → `Rebalance` → `PositionAllLocked` → `SlotsChanged` → `RebuildTrayMenu`. Rename → `SetWindowText` + `SlotsChanged` → tray + overlay update.
- **Error propagation**: Stale HWND checked via `IsWindow()` before every `SetWindowText`/`SetWindowPos`. Debounce timer handles rapid close.
- **State lifecycle risks**: Alias stored on `TileSlot` record — follows the window through reflow. No persistence across restart (can add later).
- **API surface parity**: Rename works from tray menu, overlay, AND pipe API — all three converge on `TilingCoordinator.RenameSlot()`.

## Acceptance Criteria

### Auto-grow/shrink
- [ ] Dropping 1st terminal → fullscreen
- [ ] Dropping 2nd terminal → 2-pane, existing window moves to left
- [ ] Dropping 3rd terminal → 2×2, existing windows preserve positions
- [ ] Dropping 5th terminal → 2×3
- [ ] 7th terminal on same monitor rejected (no crash, no layout change)
- [ ] Closing a terminal triggers auto-shrink (e.g., 4→3 stays 2×2, 2→1 goes fullscreen)
- [ ] Non-terminal windows (Notepad, browser) are ignored when dropped
- [ ] Rapid add/remove doesn't flicker (100ms debounce works)
- [ ] Reflow deferred while drag in progress
- [ ] Dead windows pre-validated before BeginDeferWindowPos (no crash on stale HWND)

### Multi-monitor
- [ ] Each monitor maintains independent grid (up to 6 windows each)
- [ ] Monitor identity persisted via device name (survives HMONITOR changes)
- [ ] Tiling on specific monitors can be disabled in settings
- [ ] WM_DISPLAYCHANGE refreshes monitor map
- [ ] Overlay shows on correct monitor

### Hotkey settings
- [ ] Settings window shows ComboBox with tile hotkey presets
- [ ] Changing hotkey updates config and shows "Restart to apply"
- [ ] Presets include: Ctrl+Shift+G, Shift+\`, Ctrl+Shift+Space, Win+\`
- [ ] Saved hotkey survives app restart

### Window Rename
- [ ] Tray menu shows "✏️ {alias}" per tiled window with rename action
- [ ] Right-click overlay zone opens rename dialog for that slot
- [ ] Rename dialog pre-fills current alias, OK saves, Cancel discards, Enter/Escape work
- [ ] `SetWindowText` called on rename (one-shot courtesy — terminals overwrite within ms)
- [ ] Internal alias displayed in tray menu and overlay labels
- [ ] Alias survives grid reflow (follows the window)
- [ ] Named pipe `rename` action works with success/error response
- [ ] Renaming a stale/closed window shows error, no crash
- [ ] Empty alias clears the alias (reverts to default display)

## Files Changed

| File | Change |
|------|--------|
| `Platform/Windows/TerminalDetector.cs` | **NEW** — terminal window class/process detection with GetClassName + IsWindowVisible + WS_EX_TOOLWINDOW filter |
| `Platform/Windows/TilingCoordinator.cs` | Per-monitor `MonitorGridState`, computed `GridSize`, fullscreen layout, `FindNextEmptySlot`, `RenameSlot`, `ScheduleReflow`, pre-validate + `BeginDeferWindowPos`, drag-aware reflow, `MONITORINFOEX` P/Invoke |
| `Platform/Windows/TileOverlay.axaml.cs` | Right-click → `RenameRequested` event, `HitTestLocal`, remove `CycleGridSize` |
| `Platform/Windows/HotKeyListener.cs` | Remove `OverlayCycleLayout` event + Tab polling |
| `RenameDialog.axaml` | **NEW** — rename dialog XAML |
| `RenameDialog.axaml.cs` | **NEW** — rename dialog code-behind (OnKeyDown Enter/Escape, SelectAll pre-fill) |
| `App.axaml.cs` | Per-monitor auto-assign, terminal filter, `ShowRenameDialog`, tray menu tiled window items, wire `RenameRequested`, drag-aware reflow |
| `Core/TinyBossConfig.cs` | Remove `GridSize`/`NormalizedGridSize`, add `EnabledMonitors` list |
| `SettingsWindow.axaml` | Remove grid size selector, add monitor picker checkboxes, add hotkey preset ComboBox |
| `SettingsWindow.axaml.cs` | HotkeyPreset records, ComboBox binding, restart-to-apply badge |
| `Handlers/RenameHandler.cs` | **NEW** — pipe rename handler |
| `Protocol/TinyBossEnvelope.cs` | Add `Rename` action + `RenamePayload` (with optional monitor field) |
| `Protocol/MessageHandler.cs` | Inject + dispatch `RenameHandler` |
| `TinyBossServices.cs` | Register `RenameHandler` + `TerminalDetector` |

## Sources & References

- **Origin brainstorm**: [docs/brainstorms/2026-04-25-window-rename-brainstorm.md](docs/brainstorms/2026-04-25-window-rename-brainstorm.md) — Three entry points, both SetWindowText + alias, right-click overlay zones
- **Existing dialog pattern**: `SettingsWindow.axaml` + `.cs` — Avalonia code-behind dialog
- **Existing handler pattern**: `Handlers/InjectHandler.cs` — pipe message handler with DI
- **Existing tray menu**: `App.axaml.cs:129-194` — `BuildTrayMenu()` with dynamic items
- **Existing overlay**: `Platform/Windows/TileOverlay.axaml.cs` — PointerPressed handler, zone drawing
- **TileSlot record**: `Platform/Windows/TilingCoordinator.cs:13`
- **Protocol types**: `Protocol/TinyBossEnvelope.cs:24-43`
