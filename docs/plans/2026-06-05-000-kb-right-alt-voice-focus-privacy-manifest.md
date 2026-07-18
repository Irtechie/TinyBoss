---
type: kb-manifest
kb_id: kb-2026-06-05-right-alt-voice-focus-privacy
brainstorm_path: docs/brainstorms/2026-06-05-right-alt-voice-focus-privacy-requirements.md
created: 2026-06-05
status: completed
workflow_shape: "single-skill-edit"
gate_ledger:
  - gate_id: brainstorm-to-plan
    owner_skill: kb-brainstorm
    status: passed
    required_evidence:
      - "requirements path exists"
      - "Question Gate classification exists"
      - "no unresolved ask-now or research-first items remain"
    proof:
      - docs/brainstorms/2026-06-05-right-alt-voice-focus-privacy-requirements.md
    blockers: []
    passed_at: "2026-06-05"
    allowed_next_action: "kb-plan docs/brainstorms/2026-06-05-right-alt-voice-focus-privacy-requirements.md"
  - gate_id: plan-to-work
    owner_skill: kb-plan
    status: passed
    required_evidence:
      - "manifest path exists"
      - "all slice plan paths exist"
      - "DAG has no missing blockers or cycles"
      - "each slice has acceptance criteria, expected_files, verification, test_level, functional_risk"
    proof:
      - docs/plans/2026-06-05-000-kb-right-alt-voice-focus-privacy-manifest.md
      - docs/plans/2026-06-05-001-fix-right-alt-modifier-clipboard-panic-plan.md
      - docs/plans/2026-06-05-002-fix-nonactivating-recording-indicator-plan.md
    blockers: []
    passed_at: "2026-06-05"
    allowed_next_action: "kb-work docs/plans/2026-06-05-000-kb-right-alt-voice-focus-privacy-manifest.md"
slices:
  - id: slice-001
    title: "Right Alt modifier cleanup and clipboard panic"
    path: docs/plans/2026-06-05-001-fix-right-alt-modifier-clipboard-panic-plan.md
    blockers: []
    verification: tdd
    test_level: unit
    functional_risk: narrow
    hitl: false
    status: done
    owner: agent
    blocked_reason: ""
    resume_when: ""
    next_agent_action: "Implement hotkey cleanup/panic logic and tests."
    human_action: ""
    can_continue_other_slices: true
    notes: "Done: added Right Alt cleanup metadata, synthetic modifier key-up, panic clipboard clear, and tests. Verification: dotnet build TinyBoss.csproj --no-restore; dotnet test TinyBoss.Tests/TinyBoss.Tests.csproj --no-restore (86 passed)."
  - id: slice-002
    title: "Non-activating visible recording indicator"
    path: docs/plans/2026-06-05-002-fix-nonactivating-recording-indicator-plan.md
    blockers:
      - slice-001
    verification: functional
    test_level: functional-cli
    functional_risk: narrow
    hitl: true
    status: done
    owner: agent
    blocked_reason: ""
    resume_when: ""
    next_agent_action: "Move red dot to a no-activate overlay and verify Codex focus."
    human_action: "Confirm Codex no longer flashes/loses input focus when recording starts."
    can_continue_other_slices: false
    notes: "Done: added shared no-activate/click-through window style helper, applied it to the red recording indicator, preserved TileOverlay click-through/interactivity behavior, published live build. Verification: dotnet build TinyBoss.csproj --no-restore; dotnet test TinyBoss.Tests/TinyBoss.Tests.csproj --no-restore (86 passed); installed TinyBoss health OK on 8033. Human-visible Codex focus confirmation remains recommended."
---

# KB Manifest: Right Alt Voice Focus And Privacy

## Dependency DAG

`slice-001 -> slice-002`

## Verification Summary

- Run `dotnet build TinyBoss.csproj --no-restore`.
- Run `dotnet test TinyBoss.Tests/TinyBoss.Tests.csproj --no-restore`.
- Publish/restart installed TinyBoss only during `kb-work` if live verification is requested.
- Live-check Right Alt push-to-talk in Codex and at least one CLI target.

## Scope Guard

Do not migrate to Electron. Do not remove normal paste retry behavior. Do not rely only on the notification-area tray icon for recording state.

## Work-To-Complete Gate

- status: passed
- proof:
  - `slice-001` done with build/test proof.
  - `slice-002` done with build/test/live health proof.
  - Installed TinyBoss restarted elevated and reports health OK.
- allowed_next_action: `kb-complete docs/plans/2026-06-05-000-kb-right-alt-voice-focus-privacy-manifest.md`
- residual_human_validation: Confirm Codex no longer flashes/loses focus when the red recording indicator appears.

## Completion

- review-mode: local-fallback
- review: P0=0 P1=0 P2=0 P3=0
- follow-up-resolution: resolved 0, logged 1, blocked 0
- proof:
  - 2026-06-05 `dotnet build TinyBoss.csproj --no-restore` exit 0
  - 2026-06-05 `dotnet test TinyBoss.Tests/TinyBoss.Tests.csproj --no-restore` exit 0, 86 passed
  - 2026-06-05 installed TinyBoss restarted elevated, `/health` returned `status=ok`, `/api/voice/status` returned `recording=false`, `modelLoaded=true`
- compound: skipped - small local fix; durable behavior recorded in `docs/context/PROJECT.md`
- learn: skipped - no review P0/P1 observations and no new reusable cross-repo instinct
- evolve: skipped - completion count not divisible by 5
- kb-map-refresh: done - `docs/context/PROJECT.md`
