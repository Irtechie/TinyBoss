---
kanban_id: kb-2026-05-13-mic-to-screen-reliability
slice_id: slice-004
title: "Repeatable Mic-to-Screen Harness"
blockers: [slice-001, slice-003]
verification: tdd
hitl: false
status: pending
---

# Repeatable Mic-to-Screen Harness

## What To Build

Add a repeatable local harness that exercises TinyBoss text delivery without requiring a real microphone every time. It should cover the transports that keep regressing: clipboard paste, console input, selection-mode recovery, and editable text target verification.

## Acceptance Criteria

- A test or diagnostic command can inject a known transcript into a controlled target.
- The harness can simulate paste failure or unverified paste without losing the transcript.
- The harness can reproduce console selection-mode recovery.
- CI/local tests cover policy helpers even when full Windows UI integration cannot run headless.

## Files Likely Involved

- `TinyBoss.Tests/Voice/*`
- `TinyBoss.Tests/Platform/Windows/*`
- `Voice/TextInjector.cs`
- Optional small test helper app under `TinyBoss.Tests/TestFixtures/`

## Test Scenarios

- Old clipboard + new transcript + failed paste leaves new transcript available.
- Console selection-mode detection invokes escape before delivery.
- UIA-readable edit target verifies delivered text when available.

## Scope Boundary

This harness is for delivery reliability, not Whisper accuracy. It should not require live audio input.
