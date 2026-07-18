---
type: kb-slice-plan
slice_id: slice-001
title: Right Alt modifier cleanup and clipboard panic
status: done
blocked_by: []
verification: tdd
test_level: unit
functional_risk: narrow
hitl: false
expected_files:
  - path: Platform/Windows/HotKeyListener.cs
    op: edit
    scope: "Release suppressed modifiers on voice key-up/ignored transitions and classify rapid Alt panic gesture."
  - path: Platform/Windows/VoiceHotkeyState.cs
    op: edit
    scope: "Add or expose pure state logic for rapid tap/panic classification if it belongs with hotkey state."
  - path: Voice/VoiceController.cs
    op: edit
    scope: "Keep busy-state protection; optionally expose reset path for panic cleanup if needed."
  - path: TinyBoss.Tests/Platform/Windows/VoiceHotkeyStateTests.cs
    op: edit
    scope: "Cover rapid-tap panic classification and suppressed transition safety."
  - path: TinyBoss.Tests/Voice/VoiceControllerStateTests.cs
    op: edit
    scope: "Extend busy-state tests only if controller owns any panic/reset logic."
---

# Slice 001: Right Alt Modifier Cleanup And Clipboard Panic

## Goal

Prevent stuck Alt/Ctrl state while preserving Right Alt push-to-talk. Add an intentional rapid Right Alt panic gesture that releases modifiers and clears the Windows clipboard.

## Behavior

- Normal Right Alt down/up still starts/stops dictation.
- Busy voice transitions must not start new recordings.
- Right Alt key-up and ignored suppressed events should force-release relevant modifier states.
- A rapid Right Alt hammer, likely 3-4 taps within about 4 seconds, triggers `PANIC_CLEANUP`.
- Panic cleanup clears the Windows clipboard only when it will not break an in-flight paste injection.

## Acceptance Criteria

- Unit tests cover panic classification threshold.
- Unit tests cover busy-state start blocking.
- No normal dictation clipboard retry behavior is removed.
- Logs include enough signal to distinguish normal ignored events from panic cleanup.

## Verification

```powershell
dotnet test TinyBoss.Tests/TinyBoss.Tests.csproj --no-restore
dotnet build TinyBoss.csproj --no-restore
```

Result: passed on 2026-06-05. `dotnet test` reported 86 passed.

## Risks

- Clearing clipboard during paste injection can break Codex retry behavior.
- Too-sensitive panic threshold could wipe clipboard during normal dictation attempts.
