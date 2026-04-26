---
title: "feat: Auto-collect terminals across monitors"
type: feat
status: completed
date: 2026-04-26
origin: docs/brainstorms/2026-04-26-terminal-auto-collect-rebalance-requirements.md
review: skipped-no-subagent-permission
---

# feat: Auto-Collect Terminals Across Monitors

## Problem Frame
TinyBoss should rebuild a useful workspace when it starts or when any grid overlay is invoked. Visible standalone terminals should prefer the monitor they are already on, each enabled monitor should hold at most six, and overflow should leak to the least-filled enabled monitors. Anything beyond total enabled-monitor capacity remains loose and unmanaged.

Origin: `docs/brainstorms/2026-04-26-terminal-auto-collect-rebalance-requirements.md`.

## Requirements Trace
| Requirement | Plan coverage |
|---|---|
| R1 startup collection | Unit 3 invokes collection after TinyBoss services and drag detection are initialized |
| R2 overlay collection | Unit 3 invokes collection before rendering any enabled monitor overlay |
| R3/R3a visible standalone terminals, enabled monitors only | Unit 2 centralizes terminal enumeration and enabled-monitor filtering |
| R4-R7 current-monitor preference and six-window cap | Unit 1 defines the pure assignment planner; Unit 2 applies it to grids |
| R8-R11 least-filled spillover and loose extras | Unit 1 planner handles overflow; Unit 2 leaves unassigned HWNDs unmanaged |
| R12-R14 alias preservation and no title overwrite | Unit 2 hydrates aliases from `LiveWindowAliasMemory`; no `SetWindowText` behavior is reintroduced |
| R15-R16 stable, repeatable collection | Unit 1 tie-breaks deterministically; Unit 2 avoids rewriting unchanged grids unnecessarily |

## Context & Research
No `AGENTS.md` or `CLAUDE.md` instructions exist in this repo. The relevant local patterns are:
- `App.axaml.cs` owns app lifecycle, overlay invocation, and drag orchestration.
- `Platform/Windows/TilingCoordinator.cs` owns monitor-keyed grid state, Win32 terminal discovery helpers, alias memory, process cleanup, and positioning.
- `Platform/Windows/MonitorEnumerator.cs` provides monitor device names and enabled-monitor settings can be compared against `TinyBossConfig.EnabledMonitors`.
- `Platform/Windows/TerminalDetector.cs` already defines the supported standalone terminal boundary.
- `TinyBoss.Tests/Platform/Windows/PageMovePlannerTests.cs` shows the current pattern for testing pure layout/planning logic.

External research is not needed. This is Win32 app behavior built from existing repo patterns, and current platform/library docs would not change the product or implementation strategy.

Note: the standard `ce-plan` subagent research and `document-review` passes were not run because this session only permits subagents when the user explicitly asks for delegation.

## Key Technical Decisions
- **Extract a pure collection planner.** The balancing rule is product-critical and should be deterministic under tests. Keep the planner free of HWND, Win32, Avalonia, and process APIs.
- **Prefer current monitor, then spill least-filled.** First assign each monitor's local terminals up to six. Queue each monitor's excess terminals in stable spatial order. Drain that queue into the least-filled enabled monitor with capacity.
- **Tie-break least-filled monitors by stable monitor order.** Use the enabled monitor order from `MonitorEnumerator.GetMonitors()` filtered by settings, with device-name ordering as a fallback if needed. This satisfies deterministic behavior without making monitor 1 a special destination.
- **Keep known grid order before spatial order.** When deciding which six terminals stay on an overfull monitor, keep already-managed live windows in slot order first, then fill remaining local capacity by visible window spatial order. This minimizes reshuffling and preserves existing TinyBoss intent.
- **Leave unassigned overflow untouched.** Do not move or title-change terminals that exceed total capacity; they remain loose.
- **Make global collection replace the single-monitor recovery path.** The current `ReactivateKnownWindows(monitor)` is useful but too local. Keep its detached-window protection if needed, but route startup/overlay recovery through one global collect-and-balance operation.

## High-Level Technical Design
Conceptual flow, not implementation code:

```text
CollectAndBalance(enabled monitors)
  -> enumerate visible standalone terminal windows
  -> map each HWND to its current monitor device
  -> ignore windows on disabled/unknown monitors
  -> build planner input:
       monitors in deterministic enabled order
       known grid slots/aliases for live windows
       visible terminal order by monitor and screen position
  -> planner assigns up to 6 per monitor:
       keep local known windows first
       keep local visible windows until full
       spill local overflow to least-filled monitors
       leave final overflow unmanaged
  -> rewrite affected monitor grids
  -> preserve aliases for known HWNDs
  -> register process-exit cleanup
  -> position all changed grids
  -> rebuild tray/overlay state
```

## Implementation Units

### Unit 1: Pure Terminal Collection Planner
**Goal:** Define and test monitor-capacity balancing without Win32 dependencies.

**Files:**
- `Platform/Windows/TerminalCollectPlanner.cs`
- `TinyBoss.Tests/Platform/Windows/TerminalCollectPlannerTests.cs`

**Approach:**
- Add a small pure planner similar in spirit to `PageMovePlanner`.
- Inputs should model enabled monitors in deterministic order, visible terminal candidates grouped by current monitor, and optional existing slot/order metadata.
- Output should include per-monitor assigned window ids and unmanaged overflow ids.
- Preserve current-monitor preference before spillover.
- Use least-filled monitor selection for spillover, with stable monitor-order tie-breaking.

**Execution note:** Test-first is valuable here because the planner encodes the user-visible rules.

**Test scenarios:**
- 3 terminals on monitor A and 2 on monitor B stay on their current monitors.
- 8 terminals on monitor A and 1 on monitor B becomes 6 on A, 3 on B.
- 19 terminals across 3 monitors yields 18 assigned and 1 unmanaged overflow.
- Equal least-filled recipient monitors are chosen in deterministic monitor order.
- Existing managed order is preferred over spatial-only order when choosing which six stay local.
- Disabled monitor candidates are excluded before planning, not spilled from or to.

**Verification:**
- Tests prove current-monitor preference, least-filled spillover, cap enforcement, overflow behavior, and deterministic tie-breaking.

### Unit 2: Global Collection in TilingCoordinator
**Goal:** Apply the pure plan to real visible terminals and monitor grids.

**Files:**
- `Platform/Windows/TilingCoordinator.cs`
- `Platform/Windows/MonitorEnumerator.cs` if a handle/device helper is needed
- `TinyBoss.Tests/Platform/Windows/TerminalCollectPlannerTests.cs` for pure behavior; Win32-heavy behavior remains manual/integration-verified

**Approach:**
- Add a public collection method on `TilingCoordinator`, shaped around enabled monitor device names from config.
- Enumerate enabled monitors once and keep both device names and current `HMONITOR` handles.
- Enumerate visible standalone terminal HWNDs using existing terminal detection boundaries.
- Determine each terminal's current monitor by window center. Ignore terminals on disabled or unknown monitors.
- Build planner input from current grid state plus visible terminal spatial order.
- Rewrite only changed grids where practical, then position changed monitors.
- Preserve `TileSlot.Alias` from current grid state or `_aliasMemory`.
- Clear/rewrite stale grid entries for visible collection only after pruning dead windows. Avoid clearing aliases for live HWNDs.
- Record diagnostics for collection count, assigned count, unmanaged overflow count, and per-monitor assignment counts.

**Test scenarios:**
- Pure planner coverage carries most deterministic behavior.
- Manual verification covers actual Win32 enumeration and positioning on multi-monitor setups.

**Verification:**
- No `SetWindowText` calls are added.
- Existing page-move and single-window drag behavior still route through the same per-monitor grid model.
- Diagnostics make it clear when windows were ignored because monitors were disabled or capacity was exceeded.

### Unit 3: Startup and Overlay Invocation Wiring
**Goal:** Run global collection at the product-defined moments without causing surprising overlay behavior.

**Files:**
- `App.axaml.cs`

**Approach:**
- After TinyBoss finishes service setup and monitor configuration is available, invoke global collection once on startup.
- Before `ShowOverlay()` renders an enabled monitor overlay, invoke global collection so dropped state is repaired across all monitors, not just the current monitor.
- Replace or narrow the current `ReactivateKnownWindows(_currentMonitor)` call so it does not conflict with the new global behavior.
- Keep disabled-monitor checks in `App.axaml.cs`; pass enabled monitor device names to `TilingCoordinator`.
- Ensure overlay grid size and occupied-slot labels are read after collection completes.

**Test scenarios:**
- Manual: launch TinyBoss with visible terminals on multiple monitors; grids rebuild without assuming primary/monitor 1.
- Manual: open overlay on monitor B while monitor A has over-capacity terminals; overflow uses least-filled enabled monitors.
- Manual: repeated overlay invocations converge to the same layout when windows and monitors do not change.

**Verification:**
- Tray menu reflects collected windows grouped by monitor after startup.
- Overlay occupancy reflects the post-collection grid for the invoked monitor.

### Unit 4: Preserve Existing Drag/Page-Move Behavior
**Goal:** Avoid regressions in the behavior just added for page moves and detached-window restoration.

**Files:**
- `App.axaml.cs`
- `Platform/Windows/TilingCoordinator.cs`
- Existing tests under `TinyBoss.Tests/Platform/Windows/`

**Approach:**
- Keep drag-start removal and `RestoreDetachedWindow` behavior for in-progress drags.
- Do not run global collection in drag overlay movement, because it could fight the window currently being dragged.
- Page move should continue using source and target monitor grid state after collection has populated grids.
- When collection rewrites grids, make sure detached tiles that correspond to visible terminals are either restored into the plan or discarded only if their window is gone.

**Test scenarios:**
- Existing `PageMovePlannerTests` continue to pass.
- Manual: normal drag of one terminal still assigns to the dropped slot.
- Manual: page move still piles into target monitor up to six and leaves source overflow.
- Manual: failed drag drop restores the previous grid entry.

**Verification:**
- Existing tests remain green.
- Manual multi-monitor drag flows remain unchanged except that grids are more likely to be populated before the drag.

### Unit 5: Validation and Operational Feedback
**Goal:** Make the feature observable enough to debug monitor/topology edge cases.

**Files:**
- `Platform/Windows/TilingCoordinator.cs`
- `docs/plans/2026-04-26-002-feat-terminal-auto-collect-rebalance-plan.md` status update after implementation

**Approach:**
- Add concise diagnostics to the existing drag diagnostic log for collection runs:
  - trigger: startup or overlay
  - enabled monitor count
  - visible terminal count
  - assigned count
  - loose overflow count
  - per-monitor final counts
- Keep log lines compact so normal drag diagnostics remain readable.

**Test scenarios:**
- Manual: after startup/overlay, `drag_diag.log` contains one collection summary line and per-monitor counts.

**Verification:**
- Diagnostic output can confirm whether a terminal was ignored due to disabled monitor, unsupported terminal detection, or total capacity overflow.

## System-Wide Impact
- **Startup timing:** Collection should run after config and `TilingCoordinator` are ready. It should not block first-run settings longer than needed, but a short synchronous pass is acceptable for a desktop utility.
- **Overlay correctness:** Overlay state must be read after collection to avoid stale grid counts.
- **Drag interactions:** Collection must not run during active drag movement. Startup and hotkey overlay are safe invocation points; drag overlays are not.
- **Aliases:** Alias memory remains live-window-only. Collection preserves known aliases but does not persist or infer aliases across process/window death.
- **Monitor settings:** Disabled monitors are excluded both as sources and destinations. Terminals on disabled monitors remain untouched.
- **Capacity:** The total managed capacity is `enabledMonitorCount * 6`; extra terminals remain unmanaged.

## Risks & Mitigations
- **Risk: Collection unexpectedly reshuffles windows.** Mitigation: keep local windows first, preserve existing grid order first, and make repeated runs converge.
- **Risk: Win32 enumeration misses some terminals.** Mitigation: respect existing `TerminalDetector` boundaries and log visible/assigned counts for debugging.
- **Risk: Global collection fights drag state.** Mitigation: only run on startup and interactive overlay invocation, not during drag overlay movement or drag end.
- **Risk: Disabled monitors receive overflow.** Mitigation: build planner monitors from enabled monitor list only and filter candidates before planning.
- **Risk: Rewriting grids drops aliases.** Mitigation: hydrate each assignment from existing `TileSlot.Alias` or `_aliasMemory`.

## Open Questions

### Resolved During Planning
- **Stable tie-break order:** Use enabled monitor enumeration order from `MonitorEnumerator.GetMonitors()`, with device-name ordering as fallback if the implementation needs to stabilize ambiguous enumeration output.
- **Which six stay local:** Existing managed live windows in slot order stay first; remaining local terminals are chosen in screen-spatial order.

### Deferred to Implementation
- Whether the existing private `EnumerateVisibleTerminalWindowsOnMonitor` should become a global enumerator or stay monitor-scoped behind a collection method depends on the cleanest code shape during implementation.
- Whether collection should position monitors sequentially or via a batch across monitors can be decided by keeping the current `PositionAll(monitor)` pattern unless runtime behavior shows flicker or ordering problems.

## Verification Plan
- Run `dotnet build TinyBoss.csproj`.
- Run `dotnet test TinyBoss.Tests\TinyBoss.Tests.csproj`.
- Manual validation with multiple physical monitors:
  - launch with terminals distributed across monitors;
  - launch with one overfull monitor and one sparse monitor;
  - launch with more terminals than total capacity;
  - open overlay repeatedly and confirm layout convergence;
  - confirm aliases show in TinyBoss UI and OS terminal titles are untouched;
  - confirm disabled monitors are not collected or used for spillover.

## Post-Deploy Monitoring & Validation
- Log query/search terms: `TERMINAL_COLLECT`, `GRID_RECOVERED`, `PAGE_MOVE`, `SET_WINDOW_POS FAILED`.
- Healthy signals: collection assigned count equals min(visible enabled terminals, enabled monitor count * 6); repeated overlay opens do not change per-monitor counts when windows are unchanged.
- Failure signals: unexpected loose terminals under capacity, terminals moved onto disabled monitors, aliases missing after collection, repeated reshuffle on overlay open.
- Rollback/mitigation trigger: if startup collection disrupts existing manual layouts, gate startup collection behind config while keeping overlay-triggered repair.
- Validation window: first local session after implementation; owner: TinyBoss operator/developer.
