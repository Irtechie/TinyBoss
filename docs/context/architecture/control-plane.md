# Control Plane

TinyBoss exposes a preferred named-pipe transport `pipe:TinyBoss` and a legacy loopback TCP transport on `127.0.0.1:8033`.

PitBoss connects over WebSocket with a `hello` envelope and token, then sends operations such as:

- `spawn`
- `inject`
- `window_inject`
- `kill`
- `signal`
- `introspect`
- `answer_user`
- `rename`

The introspection reply includes managed sessions and visible tiled windows. PitBoss surfaces that state through `/v1/cli`.

Visible terminal windows are first-class TinyBoss lanes even when they were not spawned by TerminalBoss. TinyBoss should provide best-effort readback for these windows:

- Classic `ConsoleWindowClass` windows are captured through the Windows console screen buffer.
- Windows Terminal/Cascadia-style windows are captured through UI Automation when the terminal exposes text.
- Title-only UI Automation text should not be treated as terminal output.

TerminalBoss is optional. Prefer deterministic TerminalBoss control endpoints when available, but PitBoss and Discord should still use TinyBoss `/v1/cli` action IDs for regular PowerShell/terminal windows.
