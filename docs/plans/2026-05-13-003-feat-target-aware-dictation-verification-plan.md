---
kanban_id: kb-2026-05-13-mic-to-screen-reliability
slice_id: slice-003
title: "Target-Aware Verification Ladder"
blockers: [slice-001]
verification: integration
hitl: false
status: pending
---

# Target-Aware Verification Ladder

## What To Build

After dictation delivery, TinyBoss should verify target acceptance where practical. Start with target types already present in the app: managed sessions, classic console windows, and UIA-readable edit controls. Unknown/browser targets can remain unverified with recovery.

## Acceptance Criteria

- Managed sessions are considered verified after stdin write succeeds.
- Classic console targets attempt verification through existing captured terminal text or a bounded post-paste text read when available.
- Native edit controls that expose a readable value/text pattern can be verified after paste.
- Browser/unknown targets are marked unverified and rely on clipboard retry.
- Logs include verification mode and result.

## Files Likely Involved

- `Voice/TextInjector.cs`
- `Platform/Windows/TerminalDetector.cs`
- `Platform/Windows/*capture*`
- `TinyBoss.Tests/Voice/*`

## Test Scenarios

- Managed session write returns verified.
- Console target in selection mode is cleared before injection and reports console transport details.
- Unknown target returns unverified, not success.

## Scope Boundary

Do not automate web-app internals in this slice. Browser editors stay OS-input plus unverified recovery unless a safe read path already exists.
