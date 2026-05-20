---
kanban_id: kb-2026-05-13-mic-to-screen-reliability
slice_id: slice-002
title: "Dictation Delivery Status and Recovery"
blockers: [slice-001]
verification: integration
hitl: true
status: pending
---

# Dictation Delivery Status and Recovery

## What To Build

TinyBoss should tell the user what happened after a recording: pasted, unverified, failed, or blocked. If delivery is failed or unverified, the recovery action should be obvious and should use the latest saved transcript.

## Acceptance Criteria

- A completed dictation has a status outcome visible through the tray/status path or existing notification surface.
- Failed injection preserves the full transcript and points the user at retry/paste recovery.
- Unverified delivery is not reported as fully successful.
- The status includes target label/process, character count, transport, and transcript id/hash.

## Files Likely Involved

- `Voice/VoiceController.cs`
- `Voice/TextInjector.cs`
- `App.axaml.cs`
- `Platform/Windows/*` tray/status code

## Test Scenarios

- Failed injection writes `voice_failed_transcripts.log` and reports a recovery status.
- Unverified clipboard paste returns a distinct status from verified delivery.
- Status does not expose transcript contents in normal logs beyond preview/hash.

## HITL Question

Confirm the exact user-facing surface: tray balloon/status text, tiny overlay on active lane, or both.

## Scope Boundary

This slice does not add deep target verification. It makes the current delivery truth visible and recoverable.
