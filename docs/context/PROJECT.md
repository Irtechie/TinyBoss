# Project Map

Bootstrap: 2026-05-23
Bootstrap confidence: mixed

## What This Is

TinyBoss is the local Windows tray app that manages terminal windows, CLI sessions, grid snapping, push-to-talk dictation, and deterministic text injection for PitBoss. PitBoss is the brain; TinyBoss is the local executor.

For planning, treat TinyBoss, OmniBoss, and TerminalBoss as one local control-surface system. PitBoss owns dispatch and state aggregation.

## How To Run

```powershell
dotnet run --project TinyBoss.csproj
Invoke-RestMethod http://127.0.0.1:8033/health
.\Installer\Build-Installer.ps1
```

## How To Test

```powershell
dotnet build TinyBoss.csproj --no-restore
dotnet test TinyBoss.Tests\TinyBoss.Tests.csproj --no-restore
```

## Current Architecture

- `Program.cs` hosts Kestrel, single-instance behavior, WebSocket endpoint, and health route.
- `App.axaml.cs` owns tray startup, menus, overlays, settings, and shutdown.
- `Core/` contains config, managed sessions, resume history, and session contracts.
- `Protocol/` contains WebSocket envelope and payload contracts.
- `Handlers/` handles spawn, inject, kill, introspect, signal, answer, rename, and window injection.
- `Platform/Windows/` owns Win32 tiling, drag watching, hotkeys, aliases, process/capture helpers, and terminal collection.
- `Voice/` owns audio capture, Whisper transcription, overlap merge, hallucination guard, and injection policy.
- `Installer/` builds the installer and elevated startup task.

## Subsystem Index

| Area | Read This | Use When | Confidence |
|---|---|---|---|
| Boss boundaries | `docs/context/architecture/boss-stack.md` | PitBoss/OmniBoss/TerminalBoss interaction | verified |
| Protocol/control plane | `Protocol/README.md`, `Handlers/README.md`, `docs/context/architecture/control-plane.md` | WebSocket envelopes, handler contracts, `/v1/cli` implications | verified |
| Window/grid control | `Platform/Windows/README.md`, `Platform/Windows/` | Tiling, aliases, terminal detection, capture | verified |
| Voice/dictation | `Voice/README.md`, `Voice/` | Push-to-talk, Whisper, paste/submit policy, target-aware dictation | verified |
| Testing | `docs/context/operations/testing.md` | Selecting deterministic checks | verified |

## Current Work Pointers

- Active TinyBoss work should be checked through PitBoss runtime backlog.
- `docs/backlog-source-of-truth.md` lists migrated TinyBoss items and explains why old plan checkboxes are historical.
- Current historical plans include dictation reliability, page move/window memory, terminal auto-collect/rebalance, and voice/tiling tray plans.

## Known Sharp Edges

- Avoid global SendKeys for TerminalBoss lanes that expose a deterministic control contract.
- Text injection should prefer paste-style paths and avoid key-repeat fallbacks that can continue after focus changes.
- Right Alt push-to-talk should be suppressed while held so it does not act as normal Alt.
- Right Alt push-to-talk must force modifier key-up cleanup after suppressed key-up/ignored transitions; rapid Right Alt hammer is the privacy panic path that clears clipboard intentionally.
- Recording indicators must be non-activating overlays. Do not use a normal topmost Avalonia window for the red dot because it can steal focus from Codex/Electron targets.
- If snapping breaks after restart, check elevation, the single-instance mutex, and stale TinyBoss processes.

## Research Index

- `docs/context/research/README.md`

## Do Not Repeat

- Do not make TinyBoss choose what work should happen; PitBoss decides.
- Do not treat old plan checkboxes as active work unless they are migrated to runtime backlog.
- Do not bypass PitBoss for normal surface-to-window control flows.

## Maintenance Notes

- Refresh this map after protocol, handler, terminal-control, dictation, installer, or run/test command changes.
