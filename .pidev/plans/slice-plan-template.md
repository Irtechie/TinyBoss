# Slice Plan Template

```yaml
---
kb_id: kb-YYYY-MM-DD-name
slice_id: slice-001
title: "Short end-to-end slice title"
blockers: []
verification: integration
test_level: integration
functional_risk: narrow
hitl: false
expected_files:
  - path: "relative/path/to/file.ext"
    op: edit
    scope: "Specific change expected in this file"
protected_oracles: []
status: pending
owner: agent
blocked_reason: ""
resume_when: ""
next_agent_action: ""
human_action: ""
can_continue_other_slices: true
---
```

## Behavior

Describe the end-to-end behavior this slice delivers.

## Acceptance Criteria

- [ ] Observable outcome one.
- [ ] Observable outcome two.
- [ ] Verification command proves the slice.

## Expected Files

List the files the executor is expected to create, edit, or delete. This is a forecast, not permission to wander.

## Verification

Command or workflow:

```powershell
<test-or-build-command>
```

## Rollback

Describe how to revert this slice if it fails.

## Handoff Notes

Record discoveries, blockers, and exact next action for the next agent.
