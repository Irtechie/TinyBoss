---
date: 2026-04-26
type: fix
status: completed
origin: docs/brainstorms/2026-04-26-reliable-long-dictation-requirements.md
---

# fix: Reliable Long Dictation Delivery

## Problem Frame

TinyBoss transcribes long voice sessions in chunks, but focused-window delivery can lose chunks while the push-to-talk key is still held. The latest live log showed multiple long Whisper chunks, followed by only the final short chunk appearing in the target. This points at delivery timing and target acceptance rather than STT loss.

## Key Technical Decision

Buffer accepted transcript chunks during the push-to-talk hold, then perform focused-window insertion only after key-up and consumer drain. This directly addresses Microsoft `SendInput` guidance that the current keyboard state can interfere with generated events, and it avoids sending paste/typing chords while Right Alt is physically down.

This is the first reliable slice of the broader verified transport ladder. It preserves the full recognized transcript and removes the proven hotkey-state failure before adding deeper target-specific verification.

## Requirements Trace

- R1, R3: preserve full recognized transcript before UI insertion.
- R2, R9, R15: update diagnostics so buffered chunks, final delivery, and target method are distinguishable.
- R4-R8, R10-R12: keep current transport selection, but avoid using focused-window transports while PTT is held.
- R13-R14: add focused tests for transcript buffering and run a manual live dictation test after deployment.

## Implementation Units

### Unit 1: Transcript Buffer

Files:
- `Voice/VoiceTranscriptBuffer.cs`
- `Voice/VoiceController.cs`
- `TinyBoss.Tests/Voice/VoiceTranscriptBufferTests.cs`

Approach:
- Add a small pure buffer object that preserves accepted chunks in order.
- Replace the ad hoc recognized text list in `VoiceController` with the buffer.
- On each accepted Whisper segment, validate guard rules, then buffer the text and log `BUFFER_APPEND`.
- On key-up, after audio stop, final segment flush, channel completion, and consumer drain, flush the buffered transcript once and inject it.

Test scenarios:
- Buffer joins trimmed segments in order.
- Buffer ignores whitespace-only segments.
- Flush clears the buffer and can be called repeatedly.

Verification:
- Unit tests pass.
- `voice_diag.log` shows `BUFFER_APPEND` during recording and one `INJECT_PENDING` after `KEY_UP`.

### Unit 2: Post-Key-Up Delivery Safety

Files:
- `Voice/VoiceController.cs`
- `Voice/TextInjector.cs`

Approach:
- Add a short key-up settle delay before final focused-window insertion.
- Keep `TextInjector` responsible for stdin, clipboard, and SendInput fallback.
- Improve focused-window diagnostics enough to identify whether final delivery used clipboard or SendInput.

Test scenarios:
- Covered indirectly by buffer tests and existing build/tests.
- Manual 40-60 second dictation into the target app should produce the full transcript instead of only the final fragment.

Verification:
- Build and test pass.
- Publish/restart TinyBoss.
- Health endpoint reports TinyBoss running.
- Live `voice_diag.log` no longer shows `INJECT_APPEND` while recording.

## Scope Boundaries

- Do not tune Whisper/VAD in this pass.
- Do not add web-app-specific automation.
- Do not claim universal delivery verification yet; this pass removes the known hotkey-state interference and preserves transcript durability.

## Deferred Follow-Up

- Add full target classification and verification ladder after this delivery-timing fix is proven live.
- Add a controlled GUI injection harness for clipboard/UIA/SendInput comparisons.
