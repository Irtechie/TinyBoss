# TinyBoss Protocol

JSON WebSocket protocol between PitBoss and TinyBoss.

The protocol still uses `KhEnvelope` names from the old KittenHerder era. The
runtime product name is TinyBoss.

## Connection

PitBoss connects to:

```text
ws://127.0.0.1:8033/ws
```

or the named pipe transport when available. The first message must be:

```json
{
  "type": "hello",
  "payload": { "token": "<tinyboss-token>" }
}
```

If no token is configured, TinyBoss runs in open dev mode.

## Message Types

| Type | Direction | Purpose |
| --- | --- | --- |
| `hello` | PitBoss -> TinyBoss | Authenticate and start the session. |
| `hello_ack` | TinyBoss -> PitBoss | Confirm readiness. |
| `spawn` | PitBoss -> TinyBoss | Start a managed process/window. |
| `inject` | PitBoss -> TinyBoss | Send text to a managed session. |
| `window_inject` | PitBoss -> TinyBoss | Send text to a watched window/slot/HWND. |
| `kill` | PitBoss -> TinyBoss | Kill a managed session. |
| `signal` | PitBoss -> TinyBoss | Send a control signal such as Ctrl+C. |
| `introspect` | PitBoss -> TinyBoss | Return sessions and tiled windows. |
| `answer_user` | PitBoss -> TinyBoss | Reply to an agent prompt. |
| `rename` | PitBoss -> TinyBoss | Rename a watched window or reapply stored names. |
| `stream_out` | TinyBoss -> PitBoss | Stream stdout/stderr from managed sessions. |
| `report` | TinyBoss -> PitBoss | Session exit/error report. |

## Rules

- Add new payload records here before changing handlers.
- Keep message names stable; PitBoss depends on them.
- Prefer additive changes over breaking changes.
