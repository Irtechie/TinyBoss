---
kanban_id: kb-2026-05-13-mic-to-screen-reliability
slice_id: slice-001
title: "Latest Dictation Clipboard Retry"
blockers: []
verification: tdd
hitl: false
status: done
---

# Latest Dictation Clipboard Retry

## What To Build

When a voice dictation completes, TinyBoss must put the newest transcript on the clipboard before attempting paste and must leave that transcript available for manual retry. A right-click/Ctrl+V after a missed paste should paste the latest recording, not an older clipboard value.

## Acceptance Criteria

- Starting a dictation paste clears/replaces prior text clipboard content before the paste chord.
- On paste chord failure, the latest dictation remains on the clipboard.
- On unverified paste success, the latest dictation remains on the clipboard.
- Previous clipboard restoration is disabled unless a later verified-delivery slice explicitly restores it after proof.
- Log messages distinguish `dictation clipboard prepared`, `paste chord sent`, and `latest transcript retained`.

## Files Likely Involved

- `Voice/TextInjector.cs`
- `Voice/VoiceController.cs`
- `TinyBoss.Tests/Voice/*`

## Test Scenarios

- Unit-test the clipboard policy through an extractable helper if direct clipboard tests are too flaky.
- Regression: simulated old clipboard + new dictation + paste failure leaves new dictation as the retry payload.
- Regression: successful unverified paste does not schedule old clipboard restoration.

## Scope Boundary

This slice does not verify whether the target consumed the paste. It only guarantees the latest transcript is the retry source.

## Verification

- Added `TinyBoss.Tests/Voice/DictationClipboardPolicyTests.cs`.
- `dotnet test .\TinyBoss.Tests\TinyBoss.Tests.csproj --no-restore --filter DictationClipboardPolicyTests`: passed, 2 tests.
- `dotnet test .\TinyBoss.Tests\TinyBoss.Tests.csproj --no-restore`: passed, 60 tests.

## Completion Notes

- `Voice/TextInjector.cs` now prepares the clipboard with the newest dictation and does not restore older clipboard text after unverified paste success or paste chord failure.
- The returned injection message now includes `dictation clipboard prepared`, `paste chord sent`, and latest-transcript retention wording so `voice_diag.log` can show the delivery policy.
