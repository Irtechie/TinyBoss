---
date: 2026-04-26
topic: reliable-long-dictation
---

# Reliable Long Dictation

## Problem Frame

TinyBoss can transcribe long voice input, but focused-window insertion is not reliable enough for real use. The current long-text path can log success while the target app only receives a tiny later chunk. This makes the user-visible failure look like transcription loss even when the transcript existed.

The feature should make dictation durable first, then target-aware. TinyBoss should never discard a recognized transcript just because one insertion transport failed.

## Research Notes

- Microsoft documents `SendInput` as synthesizing keyboard/mouse events, returning only the number of events inserted into the input stream. It is subject to UIPI, and UIPI blocking is not reliably distinguishable through return value or `GetLastError`. Source: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
- `INPUT_KEYBOARD` supports text-style input with `KEYEVENTF_UNICODE`, but Windows still delivers this through the target's keyboard/input processing path. Source: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input
- Clipboard writes require correct ownership/open/empty/set sequencing. `OpenClipboard(NULL)` followed by `EmptyClipboard` is explicitly called out by Microsoft as a case that causes `SetClipboardData` failure. Source: https://learn.microsoft.com/en-us/windows/win32/api/Winuser/nf-winuser-openclipboard
- Clipboard paste only modifies the target when the target control/app accepts paste at the current caret/selection. Standard Win32 `WM_PASTE` is for edit/combo controls, not arbitrary browser/editor surfaces. Source: https://learn.microsoft.com/en-us/windows/win32/dataxchg/wm-paste
- UI Automation `TextPattern` is read-oriented; Microsoft says text providers do not provide a way to change existing text. `ValuePattern.SetValue` can set supported editable controls, but multi-line edit/document controls often require simulated input instead. Sources: https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview and https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.valuepattern.setvalue
- Web paste is cancellable and only inserts when the cursor is in an editable context and paste is enabled. Synthetic paste events must not modify document contents. Source: https://www.w3.org/TR/clipboard-apis/

## Requirements

**Durability**
- R1. TinyBoss must keep the full recognized transcript for each push-to-talk session before attempting any UI insertion.
- R2. A transcript chunk must not be logged as successfully delivered unless TinyBoss has either a verified write path or explicitly labels the result as unverified.
- R3. If all insertion transports fail, TinyBoss must preserve the full transcript for retry/copy rather than leaving only the final spoken fragment in the target app.

**Target-Aware Insertion**
- R4. Managed CLI sessions with redirected stdin remain the preferred path; they should bypass focus, clipboard, and `SendInput`.
- R5. Focused arbitrary windows must be classified before insertion: managed CLI, visible terminal, native edit control, browser/web editor, or unknown.
- R6. For native edit controls that expose a writable UIA value, TinyBoss may use UI Automation as a direct write path.
- R7. For browser/web editors and unknown targets, TinyBoss must use a conservative simulated-input path unless clipboard paste can be verified.
- R8. Clipboard paste, when used, must use a real TinyBoss-owned clipboard window/handle, wait long enough for the target to consume paste, and restore the user's clipboard only after success/failure is known.

**Verification and Fallback**
- R9. Each insertion attempt must record target hwnd, process name, transport, character count, transcript hash/id, and verification result.
- R10. TinyBoss must attempt verification after insertion using the best target-specific read path available, such as UIA text/value capture or existing terminal text capture.
- R11. If verification fails, TinyBoss must retry through the next transport without losing ordering.
- R12. For targets that cannot expose enough text to verify, TinyBoss must mark delivery as unverified and provide an immediate recovery path with the full transcript.

**Testing**
- R13. Add a repeatable injection test harness with at least one controlled native edit target and one real-world target class used by the user.
- R14. A 60-second dictation test must confirm that all accepted Whisper chunks appear in order in the target or are preserved for retry with a visible failure state.
- R15. Regression logs must distinguish transcription success, injection attempt, paste/typing completion, verification success, verification failure, and fallback.

## Success Criteria

- Speaking for 40-60 seconds into the current target no longer produces only the final short fragment.
- `voice_diag.log` can prove whether the failure, if any, happened at transcription, transport, target acceptance, or verification.
- Full transcript recovery is available after a failed delivery.
- Clipboard state is not clobbered by failed long-dictation attempts.

## Scope Boundaries

- Do not tune Whisper/VAD unless logs show missing or rejected transcripts.
- Do not claim universal support for elevated apps unless TinyBoss is running at the same or higher integrity level.
- Do not automate web app internals directly in v1; interact through OS input surfaces and verify externally where possible.
- Do not mark unverified insertion as success.

## Key Decisions

- Treat this as a delivery reliability problem, not an STT accuracy problem: the latest logs showed recognized long chunks followed by a visible target that only received the final short chunk.
- Prefer verified stdin/direct-control paths over clipboard and raw keyboard simulation: research shows clipboard and keyboard injection can be accepted by Windows while still failing at the target application layer.
- Preserve first, inject second: the transcript is the user's data and should survive transport failures.

## Approach Options

| Option | Description | Pros | Cons |
|---|---|---|---|
| A. Throttled `SendInput` only | Remove clipboard paste and type all focused-window text in smaller paced chunks. | Simple; avoids clipboard races. | Still target-dependent; no proof; can be slow and still drop input. |
| B. Clipboard-first with better ownership | Keep clipboard paste for long text, but fix ownership/timing and restore later. | Fast when it works. | Still fails silently in some browser/editor contexts without verification. |
| C. Verified transport ladder | Persist transcript, classify target, choose transport, verify, then retry/fail with recovery. | Highest reliability and debuggability; matches research constraints. | More implementation work; needs a small harness. |

Recommendation: Option C. It turns this from a best-effort paste trick into a reliable dictation pipeline.

## Outstanding Questions

### Resolve Before Planning
- None.

### Deferred to Planning
- [Affects R5][Technical] Which target classes can TinyBoss reliably distinguish from hwnd/process/UIA data on this machine?
- [Affects R10][Technical] Which current target apps expose enough UIA/text content to verify insertion without invasive hooks?
- [Affects R13][Technical] What is the smallest controlled target harness that can reproduce clipboard, UIA, and paced typing behavior?

## Next Steps

-> `/ce-plan` for structured implementation planning.
