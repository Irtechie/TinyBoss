---
title: "feat: Move TinyBoss pages between monitors"
type: feat
status: completed
date: 2026-04-26
origin: docs/brainstorms/2026-04-26-page-move-window-memory-requirements.md
---

# feat: Move TinyBoss Pages Between Monitors

## Problem Frame
TinyBoss currently handles drag-to-grid mostly as a single-monitor workflow. The user wants a faster way to move a whole working set: hold a dedicated move-page hotkey, drag one tiled CLI to another monitor, and have the source grid pile into the target grid up to the existing six-window cap. The user also wants aliases to remain attached to live windows even when those windows temporarily leave the grid.

## Requirements Trace
| Requirement | Plan coverage |
|---|---|
| R1 dedicated configurable move-page modifier | Unit 1 adds config/settings/hotkey polling |
| R2 move source grid by dragging one tiled CLI | Unit 3 adds page-move drag mode and target detection |
| R3 preserve normal single-window drag | Unit 3 keeps the existing normal path as the default branch |
| R4-R8 target absorbs up to six, overflow stays, order preserved | Unit 2 defines the merge algorithm; Unit 3 wires it to monitor state |
| R9-R12 live-window alias memory only | Unit 4 stores aliases by live HWND and cleans them on process/window death |

## Context and Existing Patterns
- `App.axaml.cs` owns drag orchestration: `OnDragStarted`, `ShowDragOverlay`, `OnDragMoved`, and `OnDragEnded`.
- `Platform/Windows/TilingCoordinator.cs` owns slots, alias state, Win32 positioning, process-exit cleanup, and computed grid sizing.
- `Platform/Windows/HotKeyListener.cs` already combines `RegisterHotKey` for press events with `GetAsyncKeyState` polling for held voice keys. The move-page modifier should follow the polling pattern because the gesture needs held-state during drag.
- `Core/TinyBossConfig.cs`, `SettingsWindow.axaml`, and `SettingsWindow.axaml.cs` already expose configurable hotkey presets.
- `Platform/Windows/MonitorEnumerator.cs` already gives stable monitor device names, which should become the grid identity rather than transient `HMONITOR` handles.
- There is no current test project. The implementation should add a small test project for pure merge and alias-memory rules so the risky Win32 path is backed by deterministic tests.

## Key Technical Decisions
- **Use per-monitor grid state keyed by monitor device name.** The existing single `_slots` dictionary cannot support "overflow stays on the old grid"; source and target grids must coexist.
- **Use a pure page-merge helper.** The merge rule is product-critical and easy to regress, so isolate it from Win32 calls: target ordered windows stay first, source ordered windows append until capacity, overflow remains in source order.
- **Treat move-page as a drag mode, not a replacement for normal tiling.** Capture the mode at drag start from `HotKeyListener.IsMovePageHeld`; normal dragging remains unchanged when the modifier is not held.
- **Remember aliases by live HWND.** `nint hwnd` is the right identity for "current window only"; aliases are removed when TinyBoss determines the window is gone or the owning process exits.
- **Do not persist alias memory.** This follows R12 and avoids fuzzy relaunch matching.

## Resolved Planning Questions
- **Target insertion with gaps:** Before merging, compact both source and target grids into reading order. Append source windows after target windows up to six. This preserves source relative order and avoids invalid high slot numbers after grid-size recomputation.
- **Live-window identity:** Use HWND plus existing process-exit/window validation cleanup. This satisfies current-live-window memory and deliberately drops aliases after window/process death.

## High-Level Technical Design
Conceptual flow, not implementation code:

```text
Drag starts
  -> identify dragged HWND
  -> if not terminal: ignore
  -> if move-page modifier held AND HWND is currently tiled:
       capture PageMoveContext(source monitor device, dragged HWND)
       show target-monitor overlay as a move affordance
     else:
       run existing single-window drag behavior

Drag ends
  -> if PageMoveContext exists:
       target monitor = monitor at release point
       if target disabled or same as source: re-position source, no merge
       else TilingCoordinator.MovePage(source, target)
     else:
       run existing single-window slot assignment
```

The merge operation should be atomic inside `TilingCoordinator`: prune dead windows, compute target capacity, move the first N source windows, keep overflow on source, then position both affected grids.

## Implementation Units

### Unit 1: Move-Page Hotkey Configuration
**Goal:** Add a dedicated configurable move-page modifier hotkey and expose held-state to `App.axaml.cs`.

**Files:**
- `Core/TinyBossConfig.cs`
- `Platform/Windows/HotKeyListener.cs`
- `SettingsWindow.axaml`
- `SettingsWindow.axaml.cs`

**Approach:**
- Add `MovePageModifiers` and `MovePageKey` to config. Default to a non-conflicting preset such as `Ctrl+Shift+M`.
- Add move-page presets in settings near the existing tile hotkey.
- Extend conflict checks so voice, tile, and move-page presets cannot be identical.
- In `HotKeyListener`, add a public held-state property using the same `GetAsyncKeyState` style as voice push-to-talk. Do not register it as a `WM_HOTKEY`; held-state is what matters.
- Ensure `RequestReRegister()` still only controls registered tile/rebalance hotkeys.

**Patterns to follow:**
- `Core/TinyBossConfig.cs` hotkey properties
- `SettingsWindow.axaml.cs` `HotkeyPreset`, `PopulatePresets`, `CheckConflicts`
- `Platform/Windows/HotKeyListener.cs` `IsVoiceComboHeld`

**Test scenarios:**
- `TinyBoss.Tests/Platform/Windows/HotkeyConfigTests.cs`: default move-page preset does not equal default voice or tile presets.
- `TinyBoss.Tests/SettingsWindowPresetTests.cs`: conflict helper rejects identical voice/tile/move-page presets if extracted to a pure helper.

**Verification:**
- Settings can save and reload move-page hotkey values.
- Holding the configured move-page combo is observable by `App.axaml.cs`.

### Unit 2: Per-Monitor Grid State and Merge Planner
**Goal:** Replace single-monitor slot state with monitor-keyed grids and define deterministic page-merge behavior.

**Files:**
- `Platform/Windows/TilingCoordinator.cs`
- `Platform/Windows/MonitorEnumerator.cs`
- `Platform/Windows/PageMovePlanner.cs` (new)
- `TinyBoss.Tests/Platform/Windows/PageMovePlannerTests.cs` (new)

**Approach:**
- Introduce monitor grid state keyed by stable device name, while keeping `HMONITOR` handles refreshed from current display enumeration.
- Add query methods that can return a snapshot for one monitor and all monitors, so tray/overlay code can render the relevant grid.
- Add source-location lookup for a tiled HWND that returns monitor identity plus slot.
- Add `PageMovePlanner` as a pure helper: given ordered target slots and ordered source slots, return target-after, source-after, and moved count with a maximum of six target windows.
- Keep grid sizing computed from each monitor's occupied count.
- Preserve existing positioning fallback behavior in `PositionAllLocked`, but make it operate on a specific monitor grid.

**Patterns to follow:**
- Existing `GridSize` and `GetPaneBounds` behavior in `Platform/Windows/TilingCoordinator.cs`
- `MonitorEnumerator.GetDeviceName` for monitor identity
- Current `PositionAllLocked` restore/fallback logging

**Test scenarios:**
- `TinyBoss.Tests/Platform/Windows/PageMovePlannerTests.cs`: 2 source + 2 target -> target 4, source empty.
- `TinyBoss.Tests/Platform/Windows/PageMovePlannerTests.cs`: 4 source + 2 target -> target 6, source empty.
- `TinyBoss.Tests/Platform/Windows/PageMovePlannerTests.cs`: 5 source + 2 target -> target 6, source 1 remaining.
- `TinyBoss.Tests/Platform/Windows/PageMovePlannerTests.cs`: 6 source + 6 target -> no movement.
- `TinyBoss.Tests/Platform/Windows/PageMovePlannerTests.cs`: source order is preserved after append.

**Verification:**
- Two monitor grids can coexist without detaching one another.
- Source overflow remains managed and is repositioned on the original monitor.
- Target never exceeds six managed windows.

### Unit 3: Page-Move Drag Orchestration
**Goal:** Wire the held move-page modifier into drag handling without breaking normal single-window tiling.

**Files:**
- `App.axaml.cs`
- `Platform/Windows/TileOverlay.axaml.cs`
- `Platform/Windows/TilingCoordinator.cs`

**Approach:**
- Add a lightweight page-move context in `App.axaml.cs` captured at drag start only when the move-page modifier is held and the dragged HWND is currently tiled.
- In page-move mode, do not remove the dragged window from source slots at drag start.
- During drag, show overlay on enabled target monitors as a move affordance; final page move should depend on the release monitor, not a specific slot.
- On drag end, if target monitor is disabled or same as source, dismiss overlay and restore/reposition source grid.
- If target differs, call a `TilingCoordinator.MovePage(sourceDevice, targetDevice)` style operation and refresh overlay/tray state.
- Leave the existing single-window branch in place for non-modified drags.

**Patterns to follow:**
- Current `OnDragStarted` terminal filtering and diagnostic logging in `App.axaml.cs`
- Current monitor-change overlay behavior in `OnDragMoved`
- Current delayed overlay dismissal after successful drop

**Test scenarios:**
- `TinyBoss.Tests/AppDragModeTests.cs` if drag mode selection is extracted: normal drag path selected when modifier is not held.
- `TinyBoss.Tests/AppDragModeTests.cs` if extracted: page-move path selected only when modifier is held and dragged HWND is already tiled.
- Manual verification: dragging one CLI with modifier from monitor 1 to monitor 2 moves the full source grid up to target capacity.
- Manual verification: dragging with modifier to the same monitor does not scramble the grid.
- Manual verification: dragging without modifier still moves only the individual CLI.

**Verification:**
- Existing drag-to-slot behavior is unchanged without the modifier.
- Page-move behavior does not run for unmanaged or non-terminal windows.
- Diagnostics clearly distinguish single-window drag from page-move drag.

### Unit 4: Live Window Alias Memory
**Goal:** Keep aliases attached to live windows even after they leave a grid, and reapply them when they re-enter.

**Files:**
- `Platform/Windows/TilingCoordinator.cs`
- `Platform/Windows/LiveWindowAliasMemory.cs` (new, optional pure helper)
- `App.axaml.cs`
- `TinyBoss.Tests/Platform/Windows/LiveWindowAliasMemoryTests.cs` (new)

**Approach:**
- Store alias memory by HWND, separate from slot membership.
- `RenameSlot` updates both the current `TileSlot.Alias` and alias memory.
- Removing/detaching a window from a grid must not clear alias memory while the window remains live.
- Assigning a known live HWND to a grid should hydrate `TileSlot.Alias` from memory.
- Process-exit/window-dead cleanup clears both slot membership and alias memory.
- Empty alias should clear alias memory and restore default display naming.
- Review current name-change hook behavior: if alias enforcement remains, it should use alias memory as the source of truth; if it causes terminal feedback issues, keep alias display in TinyBoss UI as authoritative.

**Patterns to follow:**
- Current `RenameSlot` and `OnNameChanged` in `Platform/Windows/TilingCoordinator.cs`
- Current `RemoveSlotLocked` and process-exit cleanup
- Tray/overlay alias rendering in `App.axaml.cs` and `TileOverlay.axaml.cs`

**Test scenarios:**
- `TinyBoss.Tests/Platform/Windows/LiveWindowAliasMemoryTests.cs`: setting alias for HWND and removing from grid keeps alias retrievable.
- `TinyBoss.Tests/Platform/Windows/LiveWindowAliasMemoryTests.cs`: clearing alias removes memory.
- `TinyBoss.Tests/Platform/Windows/LiveWindowAliasMemoryTests.cs`: process/window cleanup removes memory.
- Manual verification: rename a tiled CLI, drag it out, drag it back, alias reappears.

**Verification:**
- Aliases follow live windows through remove/re-add and page moves.
- Aliases do not survive process/window exit.
- Tray and overlay labels use remembered aliases when available.

### Unit 5: Test Infrastructure and Regression Coverage
**Goal:** Add enough deterministic coverage for the new state rules without requiring real monitors in unit tests.

**Files:**
- `TinyBoss.Tests/TinyBoss.Tests.csproj` (new)
- `TinyBoss.Tests/Platform/Windows/PageMovePlannerTests.cs` (new)
- `TinyBoss.Tests/Platform/Windows/LiveWindowAliasMemoryTests.cs` (new)
- `TinyBoss.Tests/Platform/Windows/HotkeyConfigTests.cs` (new)

**Approach:**
- Add an xUnit test project referencing `TinyBoss.csproj`.
- Keep tests focused on pure helpers and config defaults.
- Leave Win32 positioning and actual drag hooks to manual verification because they depend on desktop session state.

**Verification:**
- `dotnet test` runs the new test project.
- `dotnet build TinyBoss.csproj` still passes.

## System-Wide Impact
- **State model:** This changes `TilingCoordinator` from one global grid to multiple monitor grids. Any caller that assumes `GetSnapshot()` means "the only grid" must be updated to ask for the relevant monitor or all grids.
- **Tray menu:** It should either group tiled windows by monitor or show a combined list with monitor labels. Rename actions must pass enough identity to rename the intended window.
- **Overlay:** Hotkey overlay should show the grid for the current monitor; drag overlay should show target-grid occupancy for page moves and normal occupancy for single-window moves.
- **Voice targeting:** `TileSlot.SessionId` should survive page moves so voice injection continues to identify managed CLI sessions.
- **Process cleanup:** Process-exit cleanup now affects both grid membership and alias memory.
- **Settings:** Adding a third hotkey increases conflict surface; validation must include all configurable hotkeys.

## Risks and Mitigations
- **Risk: Per-monitor refactor breaks existing single-window tiling.** Mitigation: preserve current public behaviors through compatibility wrappers where practical, and test the pure merge algorithm separately.
- **Risk: HMONITOR handles change after display topology changes.** Mitigation: key grid state by monitor device name and refresh handles before positioning.
- **Risk: Page move triggers accidentally.** Mitigation: require the dedicated held modifier and only activate when the dragged window is already tiled.
- **Risk: Alias memory leaks stale HWNDs.** Mitigation: clear memory on process-exit cleanup and prune invalid HWNDs whenever grids are read or rebalanced.
- **Risk: Manual verification is required for actual drag behavior.** Mitigation: keep Win32 work thin and put deterministic logic in pure helpers covered by tests.

## Documentation and Operational Notes
- Update any local README or docs only if they describe TinyBoss hotkeys or tiling gestures.
- Manual release validation should include two physical monitors because the core behavior depends on monitor transitions.
- Existing `drag_diag.log` entries should include page-move start/end and moved/remaining counts to make field debugging possible.

## Acceptance Criteria
- Holding the move-page modifier and dragging one tiled CLI from monitor 1 to monitor 2 moves source-grid windows into monitor 2 up to six total.
- If source has more windows than target can accept, extras remain tiled on monitor 1.
- If target already has six windows, source grid remains unchanged.
- Releasing on the same monitor does not merge or detach windows.
- Dragging without the modifier still moves only the dragged window.
- Renamed live windows keep their alias after leaving and re-entering a grid.
- Alias memory is cleared after the underlying window/process exits.
- Move-page hotkey is configurable in settings and persists in config.

## Review Notes
Inline plan review tightened these points before handoff:
- The plan explicitly resolves the two deferred planning questions from the origin document.
- The plan avoids exact implementation code and keeps Win32 behavior at design level.
- The plan adds deterministic tests for merge and alias-memory rules because the repo currently has no test project.

## Next Steps
-> /ce-work with this plan when ready to implement.
