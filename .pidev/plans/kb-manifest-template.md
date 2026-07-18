# KB Manifest Template

Use this when a planner hands executable work to Pi, Codex, Copilot, or a local model.

```yaml
---
type: kb-manifest
kb_id: kb-YYYY-MM-DD-name
source: .pidev/plans/<source-or-requirements>.md
created: YYYY-MM-DD
status: active
gate_ledger:
  - gate_id: plan-to-work
    status: passed
    proof:
      - .pidev/plans/kb-manifest-template.md
    allowed_next_action: "kb-work <manifest-path>"
slices:
  - id: slice-001
    title: "Short end-to-end slice title"
    path: .pidev/plans/slice-001-title.md
    blockers: []
    verification: integration
    test_level: integration
    functional_risk: narrow
    hitl: false
    status: pending
    owner: agent
    can_continue_other_slices: true
---
```

## Slice Overview

| Slice | Blockers | Verification | Status |
|---|---|---|---|
| slice-001 | none | integration | pending |

## Rules

- Every slice must have a plan file.
- Every blocker must reference an existing slice id.
- Do not execute until `plan-to-work` is `passed`.
- Do not hand broad workstream bullets to an executor.
