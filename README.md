# TinyBoss

TinyBoss is the local Windows tray app that manages terminal windows, CLI
sessions, grid snapping, push-to-talk dictation, and deterministic text
injection for PitBoss.

PitBoss is the brain. TinyBoss is the hands.

## What TinyBoss Owns

- Watching visible terminal/CLI windows.
- Snapping windows into monitor grids.
- Remembering user aliases for windows.
- Renaming tiled windows and reapplying names when terminals rewrite titles.
- Starting managed CLI sessions for PitBoss.
- Sending text to a managed session or watched window.
- Capturing visible window text when Windows exposes it.
- Push-to-talk local speech-to-text with Whisper.
- Tray settings and local startup behavior.

TinyBoss does not choose what work should happen. PitBoss decides and sends
commands through the TinyBoss control plane.

## Architecture

```text
PitBoss
  |
  | WebSocket JSON envelopes
  v
TinyBoss : pipe:TinyBoss + tcp:127.0.0.1:8033
  |
  | Win32, UIA, process, hotkey, audio, terminal APIs
  v
Windows terminals, apps, microphone, tray, monitors
```

## Key Paths

| Path | Purpose |
| --- | --- |
| `Program.cs` | Kestrel host, single-instance mutex, DI, WebSocket endpoint, health route. |
| `App.axaml.cs` | Tray app startup, menu, overlay interactions, shutdown, settings. |
| `Core/` | Config and managed session registry. |
| `Protocol/` | WebSocket envelope and payload contracts. |
| `Handlers/` | Spawn, inject, kill, introspect, signal, answer, rename handlers. |
| `Platform/Windows/` | Win32 tiling, drag watcher, hotkeys, window aliases, capture. |
| `Voice/` | Audio capture, Whisper transcription, VAD, injection, hallucination guard. |
| `Installer/` | Self-contained installer and elevated startup task setup. |
| `TinyBoss.Tests/` | Unit tests for config, tiling, alias memory, voice hotkey, and installer helpers. |

## Ports And Transports

| Transport | Purpose |
| --- | --- |
| `pipe:TinyBoss` | Preferred local transport. |
| `tcp:127.0.0.1:8033` | Legacy loopback transport while PitBoss migration completes. |
| `GET /health` | Health check with elevation/session/window counts. |
| `GET /ws` | WebSocket endpoint for PitBoss. |

TinyBoss should not bind port `8000`.

## Local Config

TinyBoss stores config at:

```text
%LOCALAPPDATA%\TinyBoss\tinyboss.json
```

Important settings:

| Setting | Default | Purpose |
| --- | --- | --- |
| `voiceKey` | `0xA5` | Right Alt push-to-talk key. |
| `voiceModifiers` | `0` | No modifier by default. |
| `tileModifiers` + `tileKey` | Ctrl+Shift+G | Show tile overlay. |
| `rebalanceModifiers` + `rebalanceKey` | Ctrl+Shift+R | Rebalance current grid. |
| `movePageModifiers` + `movePageKey` | Ctrl+Shift+M | Page/move grid behavior. |
| `enabledMonitors` | all | Restrict tiling to named displays. |
| `overrideSnapLayouts` | `true` | Replace Windows Snap Layouts with TinyBoss overlay. |
| `gridLayout` | `2x3` | Fill order for six-pane layouts. |
| `modelDir` | `%LOCALAPPDATA%\TinyBoss\models` | Whisper model location. |

Window aliases are stored separately:

```text
%LOCALAPPDATA%\TinyBoss\window-aliases.json
```

## Build And Test

```powershell
dotnet build TinyBoss.csproj --no-restore
dotnet test TinyBoss.Tests\TinyBoss.Tests.csproj --no-restore
```

## Run In Development

```powershell
dotnet run --project TinyBoss.csproj
```

Health:

```powershell
Invoke-RestMethod http://127.0.0.1:8033/health
```

## Build Installer

```powershell
.\Installer\Build-Installer.ps1
```

The installer output is written to:

```text
Installer\Output\TinyBoss-Installer
```

The installed app lives under:

```text
%LOCALAPPDATA%\Programs\TinyBoss
```

Installed startup uses the scheduled task:

```text
TinyBoss Elevated Startup
```

## PitBoss Protocol

PitBoss connects to TinyBoss with a `hello` envelope containing the TinyBoss
token. After that, TinyBoss handles:

- `spawn`
- `inject`
- `window_inject`
- `kill`
- `signal`
- `introspect`
- `answer_user`
- `rename`

The introspection reply includes both managed sessions and visible tiled
windows. PitBoss exposes this through `/v1/cli`.

## Operational Notes

- Visible grid windows are valid CLI lanes even if TinyBoss did not spawn them.
- If the user drags a window out of and back into a grid, TinyBoss should
  restore the remembered alias.
- Text injection should prefer paste-style paths. Avoid key-repeat fallbacks
  that can keep deleting or typing after focus changes.
- Right Alt push-to-talk should be suppressed while held so it does not act like
  a normal Alt key in target apps.
- If snapping breaks after restart, check elevation, the single-instance mutex,
  and whether an old TinyBoss process is still alive.
