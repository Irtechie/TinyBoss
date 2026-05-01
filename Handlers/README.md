# TinyBoss Handlers

Handlers execute protocol messages received from PitBoss.

Each handler should do one concrete thing and return an ack/error envelope. The
message routing switch lives in `Protocol/MessageHandler.cs`.

## Handlers

| Handler | Purpose |
| --- | --- |
| `SpawnHandler` | Starts a managed process and registers it. |
| `InjectHandler` | Sends text to a managed session. |
| `WindowInjectHandler` | Sends text to a watched window, slot, or HWND. |
| `KillHandler` | Terminates a managed session. |
| `SignalHandler` | Sends supported control signals. |
| `IntrospectHandler` | Reports managed sessions and tiled windows. |
| `AnswerUserHandler` | Replies to a waiting CLI prompt. |
| `RenameHandler` | Sets aliases or asks TinyBoss to reapply stored names. |

## Implementation Rules

- Validate payloads before touching windows/processes.
- Prefer deterministic target IDs from PitBoss `/v1/cli`.
- Do not add LLM logic here.
- Keep injection conservative. Paste/send text once; avoid key-repeat fallback
  behavior that can leak into the wrong focused app.
