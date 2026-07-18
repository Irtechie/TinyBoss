---
type: kb-slice-plan
slice_id: slice-002
title: Non-activating visible recording indicator
status: done
blocked_by:
  - slice-001
verification: functional
test_level: functional-cli
functional_risk: narrow
hitl: true
expected_files:
  - path: App.axaml.cs
    op: edit
    scope: "Make the red recording indicator visible without activating or stealing focus."
  - path: Platform/Windows/TileOverlay.axaml.cs
    op: edit
    scope: "Reuse or extract no-activate overlay style helper if needed."
  - path: Platform/Windows/
    op: create
    scope: "Optional shared Win32 no-activate window-style helper if reuse is cleaner than duplicating P/Invoke."
---

# Slice 002: Non-Activating Visible Recording Indicator

## Goal

Keep a visible red recording signal without stealing focus from Codex/Electron or other dictation targets.

## Behavior

- Red indicator remains visible on screen while recording.
- Indicator does not create a normal taskbar button.
- Indicator does not activate, focus, or capture pointer input.
- Codex input focus remains stable when recording starts.

## Acceptance Criteria

- Indicator uses Avalonia non-activation where available and Win32 no-activate styles where needed.
- Manual/live Codex test shows no focus flash when recording starts.
- CLI target dictation still works.
- Indicator closes reliably when recording stops or panic cleanup runs.

## Verification

```powershell
dotnet build TinyBoss.csproj --no-restore
dotnet test TinyBoss.Tests/TinyBoss.Tests.csproj --no-restore
```

Functional/HITL verification:

- Start TinyBoss installed/elevated.
- Focus Codex input.
- Hold/release Right Alt.
- Confirm red indicator appears and Codex input focus remains usable.
- Repeat in one CLI window.

Result: build/test passed on 2026-06-05; installed TinyBoss restarted elevated and reported health OK on `8033`. Codex visual focus confirmation remains a human validation item.

## Risks

- Avalonia `ShowActivated=false` alone may not prevent activation on all Windows backends.
- Win32 style application may need to run after the native window handle exists.
