---
date: 2026-04-26
topic: page-move-window-memory
---

# Page Move and Window Memory

## Problem Frame
TinyBoss can tile individual CLI windows into a monitor grid, but moving a whole working set to another screen is still too manual. The user wants to carry a full TinyBoss "page" from one monitor to another by dragging a representative CLI, and wants window aliases to stay attached to live windows even when they leave the grid.

## Requirements

**Page Move Gesture**
- R1. TinyBoss must support a dedicated, configurable move-page modifier hotkey.
- R2. While the move-page modifier is held, dragging any currently tiled TinyBoss-managed CLI window to another enabled monitor must move windows from the dragged window's current source-monitor grid toward the target monitor.
- R3. Normal dragging without the move-page modifier must keep the current single-window tiling behavior.

**Target Monitor Merge Behavior**
- R4. The target monitor must absorb moved CLI windows into its existing TinyBoss grid instead of replacing or swapping grids.
- R5. The target monitor must never contain more than six TinyBoss-managed windows after a page move.
- R6. If the target monitor has available capacity, TinyBoss must move source-grid windows into the target grid until the target reaches six windows or the source grid is exhausted.
- R7. If the source grid has more windows than the target can accept, the extra source windows must remain managed on the original monitor.
- R8. Moved windows must preserve their relative source-grid order when placed into open target-grid slots.

**Live Window Memory**
- R9. TinyBoss must remember aliases for live windows even when those windows leave the grid.
- R10. Remembered aliases must follow the live window, not the slot.
- R11. If a remembered live window is dragged back into a grid later, TinyBoss must show and use its remembered alias.
- R12. TinyBoss does not need to restore aliases after the underlying window or process exits.

## Behavior Examples

| Source grid | Target grid | Result |
|---|---:|---|
| 2 windows | 2 windows | Both source windows move; target becomes 4, source becomes empty |
| 4 windows | 2 windows | Four source windows move; target becomes 6, source becomes empty |
| 5 windows | 2 windows | Four source windows move; target becomes 6, one source window remains |
| 6 windows | 6 windows | No windows move; both grids remain unchanged |

## Success Criteria
- Holding the move-page modifier and dragging one managed CLI to another monitor moves as many windows from that monitor's grid as the target can accept.
- The target grid remains capped at six windows.
- Overflow windows stay tiled on the source monitor.
- Aliases remain visible for live windows after they leave and re-enter a grid.

## Scope Boundaries
- No alias restoration is required after a window or process exits.
- No page swapping behavior is required.
- No target-grid replacement behavior is required.
- No merge beyond six windows is allowed.
- Non-managed windows are not part of page move behavior.

## Key Decisions
- Use a dedicated move-page modifier hotkey: This avoids overloading the existing tile hotkey and makes page moves harder to trigger accidentally.
- Merge into target grid: This matches the user's "pile in" expectation and keeps existing target-grid windows in place.
- Cap at six windows: This preserves the current TinyBoss grid limit and avoids introducing larger layouts.
- Remember aliases only for live windows: This gives the desired "leaves the grid but keeps its name" behavior without fuzzy relaunch matching.

## Dependencies / Assumptions
- "CLI window" means a window TinyBoss already recognizes and manages through the existing tiling flow.
- Source and target monitor grids are expected to remain independently meaningful during overflow cases.

## Outstanding Questions

### Deferred to Planning
- [Affects R8][Technical] Decide exactly how to choose target insertion slots when the target grid has gaps.
- [Affects R9][Technical] Decide the live-window identity key used for alias memory while avoiding stale aliases after process exit.

## Next Steps
-> /ce-plan for structured implementation planning
