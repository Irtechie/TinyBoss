# TinyBoss Core

Core runtime state for TinyBoss.

## Files

| File | Purpose |
| --- | --- |
| `TinyBossConfig.cs` | Loads and saves `%LOCALAPPDATA%\TinyBoss\tinyboss.json`. |
| `SessionRegistry.cs` | Tracks TinyBoss-managed processes and emits session changes. |
| `ManagedSession.cs` | Session identity and metadata. |

## Notes

- Config writes are local machine state.
- Session IDs must survive PID reuse mistakes by carrying enough metadata.
- Visible grid windows may not have managed sessions; do not assume every CLI
  lane appears in `SessionRegistry`.
