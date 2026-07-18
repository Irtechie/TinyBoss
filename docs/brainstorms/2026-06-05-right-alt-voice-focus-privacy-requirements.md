# Right Alt Voice Focus And Privacy Requirements

Date: 2026-06-05

## Problem

TinyBoss uses Right Alt as push-to-talk. The current behavior can leave Windows in a logical Alt-down state during rapid key cycles, and the floating red recording indicator can steal or disturb focus in Codex/Electron targets. TinyBoss also uses the Windows clipboard for paste-based injection, which is useful for retry, but users need an intentional privacy wipe gesture.

## Decisions

- Keep Right Alt as the normal push-to-talk trigger.
- Do not migrate TinyBoss to Electron for this issue; the hard parts are native keyboard hooks, focus, and injection.
- Keep a visible recording indicator, but make it non-activating instead of relying only on the hidden notification-area tray icon.
- Add explicit modifier cleanup after TinyBoss handles or ignores Right Alt voice events.
- Add a deliberate rapid-Alt panic gesture that clears stuck modifiers and the Windows clipboard.
- Keep normal dictation clipboard retry behavior for now; do not auto-clear immediately after every dictation because manual paste retry is valuable when Codex loses focus.

## Requirements

- Holding Right Alt starts recording.
- Releasing Right Alt stops recording.
- Rapid Right Alt activity must not leave Windows with Alt logically stuck.
- Busy voice pipeline transitions must not start half-recordings.
- The red recording indicator must not activate, focus, or steal input from Codex/Electron.
- The red recording indicator must remain visible without requiring the Windows tray overflow menu.
- Rapid Alt hammer should trigger panic cleanup:
  - force Alt/Ctrl key-up cleanup
  - clear the Windows clipboard
  - reset/close recording indicator if needed
  - log `PANIC_CLEANUP`
- Clipboard clearing must not run while TinyBoss is still using clipboard transport for paste injection.

## Non-Goals

- No Electron migration.
- No Left Alt as the primary cleanup mechanism.
- No forced auto-clear of every dictation clipboard payload unless added later as a setting.
- No removal of the recording indicator unless the non-activating indicator still fails.

## Evidence

- `Core/TinyBossConfig.cs` defaults voice key to `0xA5` Right Alt.
- `Platform/Windows/HotKeyListener.cs` uses a suppressing low-level hook for voice.
- `Voice/VoiceController.cs` owns voice start/stop and busy-state behavior.
- `App.axaml.cs` currently creates a topmost Avalonia `Window` for the red dot and calls `Show()`.
- `Platform/Windows/TileOverlay.axaml.cs` already demonstrates Win32 no-activate overlay styles.

## Question Gate

- `ask-now`: none.
- `research-first`: none.
- `safe-assumption`: Win32 no-activate overlay styles can be reused or adapted for the recording indicator; wrong assumptions will be caught by Codex focus verification.
- `defer-to-planning`: exact panic gesture threshold, likely 3-4 Right Alt taps within about 4 seconds.
- `parked`: auto-clear clipboard after a timer; can be a later setting.

## Acceptance Criteria

- Right Alt push-to-talk still works in Codex, CLI, and Discord.
- Rapid Right Alt tapping does not wedge recording state or modifier state.
- Panic gesture clears clipboard and releases modifiers.
- Recording indicator remains visible and does not cause Codex focus flash.
- Tests cover hotkey busy-state and panic gesture classification where practical.
- Manual/live verification covers Codex focus behavior because unit tests cannot prove OS activation behavior.
