# TinyBoss Backlog Source Of Truth

As of 2026-04-27, active TinyBoss follow-up work is tracked by PitBoss, not by
unchecked boxes in old TinyBoss plan documents.

- Runtime backlog: `C:\ProgramData\PitBoss\pitboss.backlog.json`
- PitBoss API: `GET /v1/backlog`
- PitBoss UI: tray right-hand "Managed App Queue"

TinyBoss plans and brainstorms remain design history. If an old acceptance
criterion becomes relevant again, migrate it into the runtime backlog with:

- `app`: `tinyboss`
- `cli_type`: `grid`, `voice`, `tray`, or the target CLI family
- `type`: `reporting`, `verification`, `startup`, `routing`, or another concrete work type

## Migrated TinyBoss Items

| CLI type | Type | Status | Work |
|---|---|---|---|
| grid | reporting | next | Make per-window reporting stream one controlled TinyBoss terminal at a time |
| voice | verification | next | Run real 1-3 minute dictation tests and tighten fallback verification if text is lost |
| tray | startup | next | Verify startup/login behavior and handle second-instance activation instead of only exiting |
