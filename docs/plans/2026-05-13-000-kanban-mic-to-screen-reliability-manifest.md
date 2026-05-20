---
type: kanban-manifest
kanban_id: kb-2026-05-13-mic-to-screen-reliability
brainstorm_path: docs/brainstorms/2026-04-26-reliable-long-dictation-requirements.md
created: 2026-05-13
status: active
slices:
  - id: slice-001
    title: "Latest Dictation Clipboard Retry"
    path: docs/plans/2026-05-13-001-fix-latest-dictation-clipboard-retry-plan.md
    blockers: []
    verification: tdd
    hitl: false
    status: done
    notes: "Fix stale clipboard/right-click retry behavior."
  - id: slice-002
    title: "Dictation Delivery Status and Recovery"
    path: docs/plans/2026-05-13-002-feat-dictation-delivery-status-recovery-plan.md
    blockers: [slice-001]
    verification: integration
    hitl: true
    status: pending
    notes: "Make failures visible and recoverable without guessing."
  - id: slice-003
    title: "Target-Aware Verification Ladder"
    path: docs/plans/2026-05-13-003-feat-target-aware-dictation-verification-plan.md
    blockers: [slice-001]
    verification: integration
    hitl: false
    status: pending
    notes: "Start with terminals and UIA-readable edit controls."
  - id: slice-004
    title: "Repeatable Mic-to-Screen Harness"
    path: docs/plans/2026-05-13-004-test-mic-to-screen-harness-plan.md
    blockers: [slice-001, slice-003]
    verification: tdd
    hitl: false
    status: pending
    notes: "Prevent regressions in clipboard, console, and edit-control targets."
---

# Kanban: Mic-to-Screen Reliability

## Origin

Brainstorm: `docs/brainstorms/2026-04-26-reliable-long-dictation-requirements.md`

The completed April plan fixed one slice: buffer recognized text during push-to-talk and inject once after key-up. It did not finish clipboard freshness, visible recovery, target verification, or a repeatable injection harness.

## Slice Overview

| # | Slice | Blocked By | Verification | HITL | Status |
|---|---|---|---|---|---|
| 1 | Latest Dictation Clipboard Retry | - | tdd | no | done |
| 2 | Dictation Delivery Status and Recovery | slice-001 | integration | yes | pending |
| 3 | Target-Aware Verification Ladder | slice-001 | integration | no | pending |
| 4 | Repeatable Mic-to-Screen Harness | slice-001, slice-003 | tdd | no | pending |

## Dependency Notes

- `slice-001` is first because stale clipboard behavior is actively hurting use and is the retry substrate for every unverified target.
- `slice-002` depends on `slice-001` so the visible recovery action can trust that paste/retry uses the latest transcript.
- `slice-003` can proceed after `slice-001` because it changes confidence in delivery, not transcription.
- `slice-004` comes after the first verification paths exist so the harness tests real behavior, not just scaffolding.
