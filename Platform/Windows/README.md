# TinyBoss Windows Platform

Windows-specific implementation for tiling, hotkeys, window capture, aliases,
and drag handling.

## Major Components

| File | Purpose |
| --- | --- |
| `TilingCoordinator.cs` | Owns monitor grids, slot assignment, snap/rebalance, alias memory, and title enforcement. |
| `DragWatcher.cs` | Watches Win32 move/resize events and drives overlay snapping. |
| `TileOverlay.*` | Visual grid overlay for drag/drop and rename interactions. |
| `HotKeyListener.cs` | Global hotkeys and push-to-talk suppression. |
| `VoiceHotkeyState.cs` | State machine for PTT key transitions. |
| `TextInjector.cs` | Text delivery into focused/session targets. |
| `WindowTextCapture.cs` | Best-effort UI Automation text capture. |
| `TerminalDetector.cs` | Detects terminal-like windows. |
| `TerminalCollectPlanner.cs` | Plans visible-terminal collection into monitor grids. |
| `LiveWindowAliasMemory.cs` | Persists live HWND aliases. |
| `ElevationRelauncher.cs` | Hands non-elevated launches to the elevated startup task. |

## Tiling Rules

- Grids grow to 1, 2, 4, or 6 panes based on visible window count.
- Default six-pane layout is `2x3`.
- Fill order is top/bottom by column for `2x3`.
- Odd windows in 3- or 5-window layouts can span the remaining space.
- Dragging a managed window onto an occupied slot should swap, not snap back.

## Alias Rules

- Alias memory lives at `%LOCALAPPDATA%\TinyBoss\window-aliases.json`.
- Renaming stores the alias and applies it to the titlebar.
- Some terminals rewrite their title. TinyBoss uses a title-change hook plus a
  short reapply timer to keep aliases visible.
- Tests must use temporary alias files, never the live user alias file.

## Hotkey Rules

- Voice push-to-talk should suppress the configured key while held.
- Tile/rebalance hotkeys use `RegisterHotKey`.
- Avoid fallback behavior that keeps sending keys after focus changes.

## Diagnostics

Useful logs:

```text
%LOCALAPPDATA%\Programs\TinyBoss\drag_diag.log
%LOCALAPPDATA%\Programs\TinyBoss\crash.log
%LOCALAPPDATA%\Programs\TinyBoss\startup.log
```
