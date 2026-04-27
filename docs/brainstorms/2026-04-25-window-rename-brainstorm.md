---
date: 2026-04-25
topic: window-rename
---

# Window Rename Feature

## What We're Building

Three ways to rename a tiled window:

1. **Tray menu** — Under each tiled window entry, add a "✏️ Rename" item that opens a small Avalonia dialog with a text field.
2. **Tile overlay** — Right-click on an occupied zone to open the same rename dialog.
3. **API (PitBoss / CLI)** — Send a `rename` action over the TinyBoss named pipe: `{ "action": "rename", "slot": 0, "alias": "DRM Scheduler" }`. Any agent, script, or PitBoss command can rename windows programmatically.

Renaming does **both**:
- Calls `SetWindowText(hwnd, newTitle)` to change the real Win32 titlebar.
- Stores a TinyBoss-internal alias in the `TileSlot` record so the tray menu and overlay labels display the custom name even if the app resets its own title.

## Why This Approach

- `SetWindowText` gives immediate visual feedback — the user sees the new title in the taskbar.
- Internal alias ensures TinyBoss UI stays consistent even if the target app overwrites its title (e.g., browsers adding page names).
- Right-click on overlay zones is natural — left-click already dismisses, so right-click is an unused gesture.
- Tray menu rename provides keyboard-only access without opening the overlay.

## Key Decisions

- **Both real title + alias**: User wants both, not just one.
- **Right-click zone in overlay**: Chosen over number keys (unreliable if focused elsewhere) and inline edit (conflicts with dismiss gesture).
- **Small Avalonia dialog**: Not inline editing — a proper text field popup with OK/Cancel.
- **TileSlot gets an Alias field**: `TileSlot` record gains `string? Alias` — displayed in tray menu and overlay labels when set.

## Open Questions

- Should the alias persist across app restart (save to config)?
- Should we show the alias in the overlay zone label instead of/alongside the slot number?
- Should there be a "Reset" button to clear the alias and restore the original title?

## Next Steps

→ `/ce:plan` for implementation details
