---
title: "feat: Voice Input, Window Tiling & System Tray"
type: feat
status: historical
date: 2026-04-25
origin: docs/brainstorms/2026-04-25-voice-tiling-tray-brainstorm.md
deepened: 2026-04-25
---

# feat: Voice Input, Window Tiling & System Tray

> Historical plan. Do not use unchecked boxes in this file as active work.
> Current cross-app work is tracked by PitBoss in
> `C:\ProgramData\PitBoss\pitboss.backlog.json`.

## Enhancement Summary

**Deepened on:** 2026-04-25
**Research agents used:** 7 — Whisper.net best practices, Avalonia tray/overlay, Win32 PInvoke, Security sentinel, Architecture strategist, Performance oracle, Simplicity reviewer

### Key Design Changes from Research

1. **Voice targets KH-managed windows only (v1)** — No WindowAdopter. Eliminates the biggest complexity surface (~8-12h saved). Tiling still works for any window, but voice injection uses stdin (spawned sessions only). User confirmed.
2. **Replace TCP WebSocket with named pipes** — Security critical. `ws://localhost:8033` is vulnerable to DNS rebinding and cross-origin WebSocket hijacking. Named pipes (`\\.\pipe\TinyBoss`) are not browser-accessible and support Windows ACLs.
3. **Kill confirmation toast → hallucination guard** — Three-layer defense (RMS energy + Whisper NoSpeechThreshold + SegmentData confidence) replaces the 1.5s toast delay. Two `if` statements vs an entire toast UI.
4. **Skip class library extraction** — KittenHerder is ~10 files with one consumer. Add TinyBoss code directly or reference the exe. Extract when a second consumer appears.
5. **Skip CommunityToolkit.Mvvm** — Code-behind with manual `INotifyPropertyChanged` for the ~5 bindable properties in settings.
6. **Single persistence file** — `tinyboss.json` replaces three separate files (settings + tiles + sessions).
7. **BeginDeferWindowPos** for atomic multi-window tiling — eliminates cascade-of-repaints flicker.
8. **RegisterWaitForSingleObject** for process exit monitoring — prevents ThreadPool starvation. Handles 64 windows per OS wait thread vs blocking pool threads.
9. **Message-only window for hotkeys** — Works without any visible Avalonia window, perfect for tray apps.
10. **Mic indicator overlay** on target pane — shows 🎤 badge on whichever tiled CLI will receive voice input.

### New Risks Discovered

| Finding | Severity | Mitigation |
|---------|----------|------------|
| DNS rebinding attack on localhost WebSocket | **CRITICAL** | Named pipes instead of TCP |
| Adversarial audio → dangerous CLI commands | **CRITICAL** | Destructive command denylist + RMS/confidence guard |
| Process allowlist bypass via NTFS junctions | **CRITICAL** | Resolve symlinks with `GetFinalPathNameByHandle` |
| ONNX model can contain malicious custom operators | **HIGH** | Pin SHA-256 hashes or bundle model in installer |
| `WaitForSingleObject` on ThreadPool starves Kestrel | **HIGH** | Use `RegisterWaitForSingleObject` |
| `Process.MainWindowHandle` unreliable for Windows Terminal | **MEDIUM** | Use `EnumWindows` by PID as fallback |

### Performance Targets (All Achievable)

| Metric | Target | Expected |
|--------|--------|----------|
| Idle memory | <150 MB | 80–110 MB |
| Voice latency (warm, 3s clip) | <2s | ~850 ms |
| Cold model load | <500ms | 200–400ms |
| Startup to tray | <3s | 1.0–1.5s |

**Origin:** [brainstorm](docs/brainstorms/2026-04-25-voice-tiling-tray-brainstorm.md) — all key decisions carried forward below.

## Overview

Evolve KittenHerder from a headless WebSocket process manager into **TinyBoss** — a visible desktop companion with voice-to-CLI input (Whisper.net), configurable window tiling (2/4/6 panes), and an always-on system tray. Voice targets KH-managed sessions only (stdin injection). Tiling works for any window.

## Problem Statement

KittenHerder spawns and manages CLI sessions over WebSocket but has no visible presence. Users must type everything manually across multiple terminal windows. There's no way to organize windows quickly or dictate commands by voice. The user wants to talk to CLIs and snap windows into a grid — all managed from one tray icon.

## Architecture Decision: Startup App, Not Service

**🚨 Critical finding from research:** KittenHerder currently runs as a **Windows Service (Session 0)**. Services **cannot** show tray icons, transparent overlays, capture audio, or interact with the desktop. This is a hard Windows constraint.

**Decision:** Convert KittenHerder from a Windows Service to a **startup application** running in the user's desktop session.

**What changes:**
- Remove `UseWindowsService()` from `Program.cs` (line 7)
- Remove `Install-KittenHerderService.ps1`
- Add auto-start via registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- **Replace TCP WebSocket (`localhost:8033`) with named pipe (`\\.\pipe\TinyBoss`)** — eliminates DNS rebinding and cross-origin browser attacks (Security Critical)
- Session persistence (`sessions.json`) already handles restart recovery

**What we keep:** The existing WebSocket protocol, session registry, spawn/inject/kill handlers — all unchanged. PitBoss/Inferno reconnects via named pipe instead of TCP.

## Architecture Decision: Single Process, No Library Split

**Simplicity review finding:** Extracting KittenHerder into a class library for one consumer adds indirection with no benefit. The codebase is ~10 files.

**Decision:** Add TinyBoss code (tray, voice, tiling) directly alongside existing KittenHerder code in a single project. The entry point changes from Windows Service to Avalonia desktop app. Extract to a library only when a second consumer appears.

**Cohosting pattern (Architecture review confirmed sound):**

```
Startup Sequence:
1. Load settings (sync, fast)
2. Single-instance mutex check → signal existing instance via named pipe if duplicate
3. Start Kestrel named pipe listener (background thread, non-blocking)
4. Start Avalonia UI loop (main thread — MUST be main for Win32 message pump)
   └─ OnFrameworkInitializationCompleted:
       a. Create tray icon (programmatic, NOT XAML)
       b. Register global hotkeys (message-only window)
       c. Pre-build tray menu from SessionRegistry
       d. Voice/Whisper loads lazily on first hotkey press
5. On Avalonia exit → stop Kestrel → dispose resources
```

## Proposed Solution

### Project Structure (Single Project)

```
KittenHerder/                  (existing project — entry point changes)
├── Core/                      SessionRegistry, ManagedSession (existing)
├── Handlers/                  Spawn, Inject, Kill, Signal (existing)
├── Protocol/                  WebSocket envelope, message handler (existing)
│
├── App.axaml.cs               Tray icon (code-behind, NOT XAML — Avalonia #18493)
├── Program.cs                 Kestrel + Avalonia cohost entry point
├── Views/
│   ├── SettingsWindow.axaml   Grid size, mic selector (3 fields max)
│   └── TileOverlay.axaml      Transparent grid drop zones
├── Voice/
│   ├── AudioCapture.cs        NAudio WasapiCapture → 16kHz mono float32
│   ├── WhisperTranscriber.cs  Whisper.net lazy-load + transcribe
│   ├── HallucinationGuard.cs  3-layer defense (RMS + NoSpeech + confidence)
│   └── TextInjector.cs        stdin injection to managed sessions
├── Tiling/
│   ├── TileManager.cs         Grid layout, snap logic, BeginDeferWindowPos
│   └── TileRegistry.cs        HWND→slot tracking (composition, not in ManagedSession)
├── Platform/
│   └── Windows/
│       ├── HotKeyListener.cs  Message-only window + RegisterHotKey
│       ├── WindowInterop.cs   SetWindowPos, EnumWindows, MonitorFromPoint
│       └── NativeStructs.cs   PInvoke structs (INPUT, RECT, POINT, etc.)
│
└── KittenHerder.Tests/        (unit tests)
```

### NuGet Dependencies

| Package | Purpose |
|---------|---------|
| `Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent` | Tray icon, overlay window, settings UI |
| `Whisper.net` + `Whisper.net.Runtime` | Local speech-to-text (CPU, add GPU optionally) |
| `NAudio` | Microphone audio capture (WasapiCapture) |

**Removed:** `CommunityToolkit.Mvvm` — code-behind with manual `INotifyPropertyChanged` for ~5 bindings. No framework needed.

## Technical Approach

### Phase 1: Project Restructure & Tray Icon

Convert to startup app with Avalonia tray.

- [x] Change project output from Windows Service exe to Avalonia desktop app
- [x] New `Program.cs` — Kestrel named pipe + Avalonia cohost (Kestrel first, non-blocking)
- [x] Remove `UseWindowsService()` and service installer script
- [x] Replace TCP WebSocket with `NamedPipeServerStream("TinyBoss")` — security critical
- [ ] Add auto-start registry key (`HKCU\...\Run`)
- [x] Single-instance mutex (`Global\TinyBoss`) + named pipe activation signal
  - Second instance sends `ACTIVATE` via pipe, then exits
  - First instance brings window to foreground via `Show() → Topmost=true → Topmost=false → Activate()`
- [x] Implement tray icon **programmatically** (not XAML — avoids dual-icon bug #18493)
  - Use multi-resolution `.ico` (Assets/TinyBoss.ico already generated), not PNG
  - Set `ShutdownMode = OnExplicitShutdown` so `Window.Hide()` doesn't exit app
  - Shutdown via `Dispatcher.UIThread.Post()` (direct call freezes tray menu)
- [x] Tray right-click menu: voice target dropdown + Settings + Quit
  - Replace entire `NativeMenu` on SessionRegistry changes (safest for dynamic updates)
- [x] SessionRegistry change notifications for UI binding:
  - Add `event Action<SessionEvent> SessionChanged` to SessionRegistry
  - UI subscribes and refreshes via `Dispatcher.UIThread.Post(Refresh)`
  - `GetSnapshot()` returns `IReadOnlyList` for thread-safe UI reads
- [x] Tray dropdown dynamically lists managed sessions
  - 🎯 **Default (focused window)** ← selected by default
  - 📋 `{session.Command} (PID {session.Process.Id})` per managed session
  - Checkmark on currently selected voice target
- [ ] Verify PitBoss reconnects via named pipe
- [x] Set `ThreadPool.SetMinThreads(16, 16)` in Program.cs for burst handling

**Acceptance criteria:**
- [ ] TinyBoss starts to tray on login
- [ ] Tray shows managed sessions
- [ ] PitBoss can still spawn/inject/kill sessions via named pipe
- [ ] Only one instance can run (mutex); second instance activates first

#### Phase 1 Research Insights

**Startup sequence (Architecture review):**
```csharp
public static async Task Main(string[] args)
{
    using var mutex = new Mutex(true, "Global\\TinyBoss", out bool isFirst);
    if (!isFirst) { await SignalExistingInstance(); return; }

    ThreadPool.SetMinThreads(16, 16);

    var host = Host.CreateDefaultBuilder(args)
        .ConfigureWebHostDefaults(/* named pipe listener */)
        .Build();
    _ = Task.Run(() => host.RunAsync());  // Kestrel non-blocking

    BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    await host.StopAsync(TimeSpan.FromSeconds(5));
}
```

**Graceful shutdown sequence (Architecture review):**
1. Stop voice capture + unregister hotkeys (prevent new work)
2. Persist state to `tinyboss.json` (while services still alive)
3. Stop Kestrel with 5s timeout (drains active connections)
4. Dispose Whisper model + NAudio device
5. `desktop.TryShutdown(0)` to exit Avalonia

**Named pipe security advantages (Security review):**
- Not accessible from browsers → eliminates DNS rebinding
- Supports Windows ACLs for process-level auth
- No fixed port → no blind network attacks
- Connection rate limiting still recommended (1/sec, 3 max concurrent)

### Phase 2: Voice Input (Whisper.net)

Hold-to-talk voice transcription injected into KH-managed sessions via stdin.

- [x] Implement `HotKeyListener.cs` — message-only window approach
  - Create invisible Win32 message-only window (`HWND_MESSAGE`) for `WM_HOTKEY`
  - Works without any visible Avalonia window (perfect for tray-only state)
  - Register configurable hotkey (default: `Ctrl+Shift+Space`)
  - **⚠️ Right Alt = AltGr on international keyboards** — `RegisterHotKey` cannot distinguish L/R Alt. Use `Ctrl+Shift+Space` as safer default. If Right Alt desired, use low-level keyboard hook (`WH_KEYBOARD_LL`) instead.
  - Conflict detection: if `RegisterHotKey` returns FALSE (error 1409), alert user
  - Store hotkey preference in `tinyboss.json`
  - `MOD_NOREPEAT` flag prevents held-key spam
- [x] Implement `AudioCapture.cs` — NAudio WasapiCapture
  - Use `WasapiCapture` in shared mode — let WASAPI choose native format, then resample
  - Resampling chain: device native → `StereoToMonoSampleProvider` → `WdlResamplingSampleProvider(16000)` → float32 accumulator
  - **Never set `WasapiCapture.WaveFormat` directly** — WASAPI shared mode requires device's native format
  - Re-acquire device on each recording start (handles Bluetooth hot-swap)
  - Accumulate samples in `List<float>` with lock; return `float[]` on stop
  - If no audio device: show error toast, disable voice
  - **Dispose pattern:** `StopRecording()` THEN `Dispose()` — reverse order causes `AccessViolationException`
- [x] Implement `WhisperTranscriber.cs` — single Factory, single Processor, reuse
  - Lazy-load `ggml-base.en` model on first hotkey press (~74 MB disk, ~350 MB RAM)
  - Download from Hugging Face with atomic write (`.tmp` → `File.Move`) and progress toast
  - **Pin SHA-256 hash** of expected model file — reject mismatches (security: model tampering)
  - Cache at `%LOCALAPPDATA%\TinyBoss\models\ggml-base.en.bin`
  - Create one `WhisperFactory` → one `WhisperProcessor` → reuse across all PTT presses
  - `ProcessAsync` is internally serialized via `SemaphoreSlim(1,1)` — thread-safe
  - **60-second idle timeout:** after no PTT for 60s, `Dispose()` factory + `GC.Collect(2, Aggressive)` to reclaim ~300 MB. Cold reload from SSD takes ~200-400ms.
  - Optimal config: `.WithLanguage("en").WithSingleSegment(true).WithNoSpeechThreshold(0.4f).WithThreads(4)`
  - **Batch mode** (not streaming) — single-pass on key release. Better accuracy, lower CPU.
- [x] Implement `HallucinationGuard.cs` — 3-layer defense (replaces confirmation toast)
  - **Layer 1 (pre-inference):** Reject if audio < 500ms or RMS energy < 0.008. Whisper hallucinates on silence ("Thanks for watching!", "Subscribe!", "♪").
  - **Layer 2 (Whisper config):** `.WithNoSpeechThreshold(0.4f)` (more aggressive than default 0.6)
  - **Layer 3 (post-inference):** Check `SegmentData.NoSpeechProb > 0.4`, `AverageLogProb < -1.0`, text ≤ 2 chars, and known hallucination phrases denylist
  - **Layer 4 (destructive command denylist):** Regex check for dangerous patterns (`rm -rf`, `del /f /s`, `format`, `shutdown`, `curl|bash`, `powershell -enc`, etc.). Match → require explicit click confirmation (not timed). This is the security-critical defense.
- [x] Implement `TextInjector.cs` — stdin injection to managed sessions
  - Voice targets **KH-managed sessions only** (v1 scope decision)
  - Use existing `InjectHandler` path — writes to `Process.StandardInput`
  - No `SendInput` needed for v1 — stdin is immune to UIPI/focus-stealing
  - If target = "Default (focused)": identify which managed session has focus via `GetForegroundWindow` → match PID to SessionRegistry
  - If no managed session has focus, show brief toast "No managed session focused"
  - 🎤 badge overlay on the target pane when tiled (visual indicator of voice target)
- [x] Tray icon indicator: red overlay while recording, normal when idle
- [x] Raw transcription only — no command interpretation, no punctuation conversion

**Voice flow (simplified — no toast delay):**
```
Hold Ctrl+Shift+Space → tray 🔴 → AudioCapture.Start()
Release → AudioCapture.Stop() → HallucinationGuard.ShouldTranscribe(samples)?
  → NO: discard silently, tray normal
  → YES: WhisperTranscriber.TranscribeAsync(samples)
    → HallucinationGuard.IsValidSegment(segment)?
      → NO: discard
      → YES: DestructiveCommandCheck(text)?
        → DANGEROUS: show click-to-confirm dialog
        → SAFE: TextInjector.InjectViaStdin(targetSession, text)
  → tray returns to normal
```

**Acceptance criteria:**
- [ ] Hold hotkey → speak → release → text appears in managed PowerShell session
- [ ] Tray turns red while recording
- [ ] Silence/short press produces no injection (3-layer hallucination guard)
- [ ] Dangerous commands require click confirmation
- [ ] First use downloads model with progress indication
- [ ] Hotkey is configurable in settings
- [ ] 🎤 indicator shows on target pane when tiled

#### Phase 2 Research Insights

**Whisper.net lifecycle (Whisper research):**
- One Factory → one Processor → many calls. Do NOT recreate per PTT press.
- `WhisperFactory.Dispose()` calls `ggml_free()` — releases native memory immediately (not GC-dependent)
- Error taxonomy: `FileNotFoundException` (missing model), `WhisperModelLoadException` (corrupt), `DllNotFoundException` (missing runtime), `OutOfMemoryException` (model too large)

**NAudio capture (Whisper research):**
```csharp
// CORRECT: Let WASAPI choose native format, resample afterward
var capture = new WasapiCapture(device);  // Don't set WaveFormat!
ISampleProvider pipeline = buffered.ToSampleProvider();
if (channels > 1) pipeline = new StereoToMonoSampleProvider(pipeline);
if (sampleRate != 16000) pipeline = new WdlResamplingSampleProvider(pipeline, 16000);
```

**Performance (Performance review):**
- Voice latency budget: capture ~30ms + inference ~800ms (3s clip) + injection ~20ms = **~850ms warm** ✅
- Cold start with model load: +200-400ms = **~1.2-1.7s** ✅ (within 2s target)
- Memory: ~400-450 MB during voice, drops to ~80-110 MB after 60s idle unload

**Security (Security review):**
- Never inject into elevated windows (v1 uses stdin, so UIPI not a concern)
- Audio data lifecycle: never write raw audio to disk, process in memory, discard after transcription
- Stop capturing when workstation locked (`WTS_SESSION_LOCK`)

### Phase 3: Window Tiling

Configurable grid overlay (2/4/6 panes) with drag-to-snap. Voice targets KH-managed windows only; tiling works for any window.

- [x] Register tiling hotkey (default: `Ctrl+Shift+G` — avoids Win+G Xbox Game Bar conflict)
  - Use same `HotKeyListener` message-only window from Phase 2
  - Detect Game Bar conflict on startup and warn user
- [x] Implement `TileOverlay.axaml` — transparent Avalonia window
  - `TransparencyLevelHint = Transparent`, `Background = Transparent`, `SystemDecorations = None`, `Topmost = true`
  - Root container `IsHitTestVisible="False"` (click-through), interactive zones `IsHitTestVisible="True"`
  - Fullscreen on cursor's monitor: `GetCursorPos → MonitorFromPoint → MaximizeOnScreen`
  - **Position BEFORE WindowState** — Avalonia maximizes on whichever screen contains current position
  - Semi-transparent colored rectangles for drop zones (Skia renders trivially — <1ms on 4K)
  - **Configurable pane count: 2, 4, or 6** (persisted in `tinyboss.json`, default: 4)
    - **1–2 windows:** side-by-side (2 panes, 50/50)
    - **3–4 windows:** 2×2 grid (4 panes)
    - **5–6 windows:** 2×3 grid (6 panes)
    - Odd counts (3, 5) use the next grid up — empty slots remain available
  - Cycle layout while overlay is open: press `Tab` to switch 2→4→6→2
  - **Overlay stays open** after each drop until all slots filled or user dismisses
  - Dismiss on: Escape, click outside, hotkey again (toggle), or all slots filled
  - DPI-aware: use `Screen.WorkingArea` (physical pixels, already excludes taskbar)
- [x] Implement `TilingCoordinator.cs` — unified snap logic + registry + process monitoring
  - Calculate pane bounds from `rcWork` (working area via `GetMonitorInfo`)
  - **`BeginDeferWindowPos` / `EndDeferWindowPos`** for atomic multi-window moves — eliminates cascade-of-repaints flicker
  - `SWP_NOZORDER | SWP_NOACTIVATE` flags — don't steal focus during batch tile
  - Track which HWND is in which slot via serialized lock (composition pattern — not in ManagedSession)
  - Allow re-arranging (drop a new window to replace existing occupant)
  - **On window close: leave the gap.** Add `Ctrl+Shift+R` manual rebalance hotkey. Auto-rebalance is disorienting (windows move unexpectedly).
  - IsWindow() validation before every operation
- [x] Window drag detection — `SetWinEventHook` approach (not `WH_MOUSE_LL`)
  - Hook `EVENT_SYSTEM_MOVESIZESTART` / `EVENT_SYSTEM_MOVESIZEEND` — fires with HWND of window being dragged
  - Hook `EVENT_OBJECT_LOCATIONCHANGE` during drag for live zone highlighting (throttle to 16ms)
  - On drop: hit-test cursor position against overlay zones, snap if match
  - `WINEVENT_SKIPOWNPROCESS` to ignore own overlay
- [x] Process exit monitoring — `RegisterWaitForSingleObject` (NOT `WaitForSingleObject` on ThreadPool)
  - Uses OS wait thread pool: 64 handles per thread, zero .NET ThreadPool starvation
  - On exit: remove from TilingCoordinator, update tray dropdown, leave gap in grid
- [x] For HWND discovery: `EnumWindows` by PID as fallback when `Process.MainWindowHandle` returns zero
  - Filter: `IsWindowVisible`, not owned (`GW_OWNER`), not `WS_EX_TOOLWINDOW`
  - `Process.MainWindowHandle` unreliable for Windows Terminal and UWP apps
- [x] Grid size persisted via `tinyboss.json` (TinyBossConfig)
- [x] IsWindow() validation on all HWND operations

**Tiling flow:**
```
Ctrl+Shift+G → GetCursorPos → MonitorFromPoint → show TileOverlay on that monitor
  → shows grid (2/4/6 slots based on setting) — press Tab to cycle layout
  → user drags window over overlay → SetWinEventHook detects drag
  → cursor enters zone → zone highlights
  → user releases → SetWindowPos snaps window to zone (via BeginDeferWindowPos)
  → overlay stays open for more windows
  → repeat until all slots filled OR Escape / click outside / hotkey toggle
  → tray dropdown updates
```

**Acceptance criteria:**
- [ ] Hotkey shows transparent grid overlay on current monitor
- [ ] Dragging any window into a slot snaps it; overlay stays open for more
- [ ] Tab cycles between 2/4/6 pane layouts while overlay is open
- [ ] Pane count preference persists across sessions
- [ ] Snapped KH-managed windows can receive voice input
- [ ] Window close leaves a gap; Ctrl+Shift+R rebalances
- [ ] Works on multi-monitor with mixed DPI
- [ ] No ThreadPool starvation with 6+ monitored windows

#### Phase 3 Research Insights

**Atomic window positioning (Win32 research):**
```csharp
var hdwp = BeginDeferWindowPos(layout.Count);
foreach (var (hwnd, rect) in layout)
    hdwp = DeferWindowPos(hdwp, hwnd, IntPtr.Zero,
        rect.Left, rect.Top, rect.Width, rect.Height,
        SWP_NOZORDER | SWP_NOACTIVATE);
EndDeferWindowPos(hdwp);  // All moves happen in one
```

**DPI coordinate rules (Avalonia + Win32 research):**
| Property | Units |
|----------|-------|
| `Screen.Bounds` / `WorkingArea` | Physical pixels |
| `Window.Position` | Physical pixels |
| `Window.Width/Height` | Logical (DIPs) |
| `rcWork` from `GetMonitorInfo` | Physical pixels (use directly with SetWindowPos) |

**UIPI note (Win32 research):** `SetWindowPos` works fine across integrity levels — only `SendInput`/`SendMessage` are blocked. Tiling operations work regardless of elevation. Since v1 voice uses stdin (not SendInput), UIPI is not a concern for voice either.

**Process exit monitoring (Performance review):**
```csharp
// Uses OS wait thread pool — 64 handles per thread, zero .NET pool starvation
ThreadPool.RegisterWaitForSingleObject(
    processHandle, (state, timedOut) => {
        if (!timedOut) OnWindowProcessExited((nint)state!);
    }, windowHandle, Timeout.Infinite, executeOnlyOnce: true);
```

### Phase 4: Settings & Polish

Minimal settings UI — sane defaults for everything, config file for power users.

- [x] Settings window (Avalonia) — **3 fields only for v1:**
  - Grid size selector (2/4/6 pane dropdown)
  - Microphone selector (enumerate audio devices, default = system default)
  - Voice hotkey display (shows current hotkey)
- [x] Everything else lives in `tinyboss.json` for power users:
  - Tiling hotkey (default: `Ctrl+Shift+G`)
  - Rebalance hotkey (default: `Ctrl+Shift+R`)
  - Whisper model path (default: `%LOCALAPPDATA%\TinyBoss\models\`)
  - Auto-start (default: on) — power users edit registry directly or set in JSON
- [x] Persist all settings to single file: `%LOCALAPPDATA%\TinyBoss\tinyboss.json`
  - Atomic writes: write to `.tmp` then `File.Replace` — prevents corruption on crash
  - Sessions are ephemeral (PIDs die on restart) — reconstructed from KittenHerder on startup
- [x] First-run experience: show settings on first launch for mic selection
- [ ] Error handling: toast notifications for failures (mic unavailable, model download failed, etc.)
- [ ] Process allowlist hardening:
  - Store full absolute paths (not bare exe names)
  - Resolve symlinks with `GetFinalPathNameByHandle` before allowlist check
  - Consider SHA-256 hash verification of spawned binaries

**Acceptance criteria:**
- [x] Settings window opens from tray menu
- [x] Grid size change takes effect immediately
- [x] Mic selector shows available devices
- [x] Settings persist across restart

## System-Wide Impact

### Interaction Graph

- TinyBoss hosts both the Kestrel named pipe server and the Avalonia UI in one process
- PitBoss → named pipe → existing handlers (protocol unchanged, transport changed)
- Tray → SessionRegistry (with `SessionChanged` events) → tray dropdown
- Voice → AudioCapture → HallucinationGuard → Whisper → TextInjector → `InjectHandler` (stdin)
- Tiling → TileOverlay → TileRegistry → `SetWindowPos` (via `BeginDeferWindowPos`)
- Threading: Kestrel on ThreadPool, Avalonia on dedicated UI thread, never cross without `Dispatcher.UIThread.Post`

### Error Propagation

- Whisper transcription failure → toast notification, no injection (safe)
- Audio device lost mid-recording → `NAudio.RecordingStopped` event → stop recording, toast error, auto-retry with backoff
- Hallucination detected → silently discarded (3-layer guard)
- Dangerous command detected → click-to-confirm dialog (blocks injection until user acts)
- Named pipe disconnect from PitBoss → existing reconnect logic (unchanged)
- HWND stale after process exit → validate with `IsWindow()` PInvoke before use

### State Lifecycle

- **Tiled window closes** → `RegisterWaitForSingleObject` callback fires → remove from TileRegistry → leave gap in grid → tray updates
- **TinyBoss crash** → `tinyboss.json` persists settings; session/tile state is ephemeral (reconstructed on restart)
- **Workstation locked** → stop mic capture (`WTS_SESSION_LOCK`), resume on unlock

### API Surface Changes

- **Transport:** TCP WebSocket → Named pipe (PitBoss needs corresponding update)
- **No new WebSocket messages** — voice/tiling status messages cut (YAGNI — no consumer exists)

## Scope Boundaries

**In scope (v1):**
- Windows only
- Avalonia tray + voice + tiling in single process
- Voice → stdin injection to KH-managed sessions only
- Whisper.net CPU inference, ggml-base.en model
- Configurable hotkeys and grid size
- Named pipe IPC (replaces TCP WebSocket)

**Explicitly out of scope (v1 — deferred):**
- WindowAdopter (voice to arbitrary non-KH windows via SendInput) — v2 when users ask
- macOS support (Avalonia is cross-platform but Win32 APIs aren't)
- Voice command interpretation ("kill session 3") — raw dictation only
- GPU inference selection UI
- Wake word / always-listening mode
- Auto-rebalance on window close (leave gap + manual rebalance instead)
- Rich settings UI (hotkey picker widget, model manager, auto-start toggle)
- PitBoss-triggered voice recording (privacy concern)
- Cross-monitor drag during tiling

## Security Hardening Checklist

| Priority | Item | Status |
|----------|------|--------|
| 🔴 P0 | Named pipe instead of TCP WebSocket | Planned (Phase 1) |
| 🔴 P0 | Destructive command denylist + click-confirm | Planned (Phase 2) |
| 🔴 P0 | Full-path allowlist with symlink resolution | Planned (Phase 4) |
| 🟡 P1 | Pin model SHA-256 hash | Planned (Phase 2) |
| 🟡 P1 | DPAPI for any secrets in settings | Planned (Phase 4) |
| 🟡 P1 | Audio: never write to disk, stop on lock | Planned (Phase 2) |
| 🟢 P2 | Connection rate limiting on named pipe | Deferred |
| 🟢 P2 | Authenticode signing | Deferred |
| ⚪ P3 | NuGet dependency pinning | Deferred |

## Dependencies & Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Win+G conflict with Xbox Game Bar | High | Default hotkey fails | Default to Ctrl+Shift+G, make configurable |
| AltGr = Ctrl+RightAlt on international keyboards | Medium | RegisterHotKey can't distinguish L/R Alt | Use Ctrl+Shift+Space default (not Right Alt) |
| Whisper hallucination on silence/noise | Medium | Phantom text injected into terminal | 4-layer defense: RMS + NoSpeech + confidence + denylist |
| DNS rebinding on localhost WebSocket | High | Remote code execution | Named pipe instead of TCP (eliminated) |
| 300MB model download on first use | Low | Poor first-run experience | Progress toast, atomic download with resume |
| Avalonia dual-icon bug (#18493) | Low | Two tray icons appear | Code-behind creation, not XAML |
| ThreadPool starvation from blocked waits | Medium | Kestrel stops responding | RegisterWaitForSingleObject (OS wait threads) |
| Process.MainWindowHandle returns 0 | Medium | Can't find window for tiling | EnumWindows by PID fallback |
| PitBoss needs named pipe update | High | Breaking change for PitBoss | Coordinate with PitBoss update |

## Sources & References

### Origin

- **Brainstorm:** [docs/brainstorms/2026-04-25-voice-tiling-tray-brainstorm.md](docs/brainstorms/2026-04-25-voice-tiling-tray-brainstorm.md)

### Internal References

- `Program.cs:7` — `UseWindowsService()` to remove
- `Core/SessionRegistry.cs:52-70` — session persistence (add change events)
- `Core/ManagedSession.cs:18-23` — leave unchanged; tile data in separate TileRegistry
- `Handlers/SpawnHandler.cs:60-89` — visible window spawning pattern
- `Handlers/InjectHandler.cs:48-59` — stdin injection (reuse for voice)
- `Protocol/KittenHerderEnvelope.cs:24-43` — extensible message types

### Cross-Project Learnings

- MyBuddy Avalonia tray pattern — programmatic creation, `.ico` format, `ShutdownMode`, `Dispatcher.Post()` for shutdown
- MyBuddy `bridge/python_worker.py:108-176` — existing Whisper implementation (Python, for reference)
- MyBuddy launcher plan — single-instance mutex, health monitoring pattern

### External References

- [Whisper.net v1.9.0](https://github.com/sandrohanea/whisper.net) — C# whisper.cpp bindings
- [NAudio 2.2](https://github.com/naudio/NAudio) — .NET audio capture (WasapiCapture)
- [Avalonia 11.x TrayIcon docs](https://docs.avaloniaui.net/docs/concepts/services/tray-icon) — dual-icon bug, programmatic creation
- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) — global hotkeys
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) — text injection (v2, not v1)
- [BeginDeferWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-begindeferwindowpos) — atomic multi-window positioning
- [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook) — window drag detection

### Research Agents (Deepening)

| Agent | Key Contribution |
|-------|-----------------|
| Whisper.net researcher | Model lifecycle, 3-layer hallucination guard, NAudio pipeline, thread safety rules |
| Avalonia researcher | Message-only window for hotkeys, overlay hit-testing, Kestrel cohost pattern, single-instance mutex |
| Win32 researcher | BeginDeferWindowPos, SetWinEventHook drag detection, UIPI integrity check, EnumWindows filtering |
| Security sentinel | Named pipe recommendation, destructive command denylist, model hash pinning, audio privacy |
| Architecture strategist | Startup/shutdown sequences, SessionRegistry events, composition over ManagedSession modification |
| Performance oracle | RegisterWaitForSingleObject, ThreadPool.SetMinThreads, 60s model idle unload, batch-mode Whisper |
| Simplicity reviewer | Cut WindowAdopter, kill toast, skip MVVM framework, single JSON file, leave-the-gap tiling |
