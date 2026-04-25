# KittenHerder: Voice Input, Window Tiling & System Tray

**Date:** 2026-04-25
**Status:** Brainstorm complete — ready for planning

## What We're Building

Evolving KittenHerder from a headless WebSocket process manager into a visible desktop companion with three integrated capabilities:

1. **System Tray App** — Always-on tray icon with dropdown showing all managed windows. Right-click menu lets you pick which window receives voice input (or "Default" = focused window).

2. **Voice-to-CLI (Whisper.net)** — Hold Right Alt → 🔴 tray indicator → speak → release → Whisper.net transcribes locally → text injected into the selected/focused window. No cloud API, fully offline, ~300MB model lazy-loaded on first use.

3. **4-Quadrant Window Tiling** — Press Win+G → transparent overlay grid appears on the current monitor with 4 drop zones. Drag any window into a quadrant → it snaps into place and KittenHerder "adopts" it as a managed session. Adopted windows appear in the tray dropdown as voice targets.

## Why This Approach

- **Voice lives in KittenHerder** because it already manages CLI windows and runs as an always-on service (~70MB idle). Adding voice here avoids a new app, new port, new service.
- **Whisper.net (C# native)** keeps KittenHerder self-contained — no Python sidecar needed. The `faster-whisper` in MyBuddy is Python-only; Whisper.net gives us native .NET bindings to whisper.cpp.
- **Avalonia UI** for tray icon + transparent overlay because it's cross-platform (Windows + macOS), .NET native, and supports transparent windows and system tray. Platform-specific code for global hotkeys and window management (Win32 on Windows, AppKit on macOS).
- **Window adoption via tiling** is elegant: dragging a window into the grid is both a layout action and a "register with KittenHerder" gesture. No separate enrollment step.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Voice activation | Hold Right Alt (configurable) | Bottom of keyboard, rarely used, not taken by popular apps |
| Tray indicator | 🔴 red dot while recording | Clear visual feedback |
| Whisper engine | Whisper.net (C# whisper.cpp bindings) | Self-contained, no Python dependency, GPU-accelerated |
| Whisper model | base.en (~300MB, lazy-loaded) | Fast for short commands, English-only is fine |
| Voice target | Dropdown: Default (focused) or pick managed window | Flexible — talk to anything or target specific CLIs |
| Tiling hotkey | Win+G on target monitor | Shows 4-quadrant overlay on that monitor only |
| Tiling behavior | Drag windows into overlay slots | Intuitive, also "adopts" window into KH management |
| UI framework | Avalonia + platform-specific native code | Cross-platform (Win+Mac), .NET native, transparent overlay support |
| Architecture | Add to KittenHerder (no new app) | Already always-on, already manages windows, ~70MB idle |

## Architecture

```
KittenHerder (always-on .NET service)
├── Core/               (existing: session registry, process management)
├── Handlers/           (existing: spawn, inject, kill, signal)
├── Protocol/           (existing: WebSocket envelope protocol)
├── Tray/               (NEW: Avalonia system tray, dropdown menu)
│   ├── TrayIcon        — managed window list, voice target selector
│   └── Settings UI     — hotkey config, Whisper model, mic selection
├── Voice/              (NEW: Whisper.net integration)
│   ├── AudioCapture    — mic recording on hotkey hold (NAudio or similar)
│   ├── Transcriber     — Whisper.net model, lazy-loaded, GPU if available
│   └── Injector        — SendInput/Win32 to inject text into target window
├── Tiling/             (NEW: window management overlay)
│   ├── GridOverlay     — Avalonia transparent window, 4 drop zones
│   ├── WindowSnapper   — Win32 SetWindowPos to snap dragged windows
│   └── WindowAdopter   — register non-KH windows as managed sessions
└── Platform/           (NEW: OS-specific implementations)
    ├── Windows/        — global hotkeys, Win32 window APIs, SendInput
    └── macOS/          — AppKit hotkeys, NSWindow management (future)
```

## Voice Flow

```
User holds Right Alt
  → Tray goes 🔴
  → AudioCapture starts recording from default mic
User releases Right Alt
  → Tray goes back to normal
  → Audio buffer sent to Whisper.net Transcriber
  → Transcribed text determined
  → If target = "Default": inject into focused window via SendInput
  → If target = specific session: inject via KH WebSocket `inject` message
```

## Tiling Flow

```
User presses Win+G
  → GridOverlay appears on current monitor (transparent, 4 quadrants)
  → Each quadrant shows a labeled drop zone
User drags a window into quadrant
  → Window snaps to quadrant bounds via SetWindowPos
  → KittenHerder "adopts" the window (adds to managed sessions)
  → Window appears in tray dropdown as voice target
User dismisses overlay (Escape or click outside)
  → Overlay closes, tiled windows stay in place
```

## Integration with Existing Ecosystem

- **PitBoss (Logos)** — already connects via WebSocket. PitBoss can spawn sessions that appear in the tray dropdown and are voice-targetable.
- **MyBuddy** — AI-routed voice goes through MyBuddy → PitBoss → KittenHerder → target CLI. Direct voice (hold Right Alt) is KittenHerder-only, no AI routing.
- **Inferno** — Inferno sessions already show in KH. They'll appear in tray dropdown and be voice-targetable.

## Open Questions

None — all key decisions resolved during brainstorm.

## Phasing

**Single phase — full vision:**
- Avalonia tray icon with managed window dropdown
- Whisper.net voice transcription on configurable hotkey (default: Right Alt)
- 4-quadrant transparent overlay with drag-to-snap
- Window adoption into KH management
- Voice routing to focused window or selected managed window
- Platform-specific native code for hotkeys/window management (Windows first, macOS later)
