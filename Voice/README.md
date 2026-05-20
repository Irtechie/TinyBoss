# TinyBoss Voice

Local push-to-talk speech-to-text pipeline.

TinyBoss voice is local. It captures microphone audio while push-to-talk is
held, transcribes larger overlapped chunks with Whisper after key-up, filters
hallucinations/dangerous text, then injects a single final text payload.

## Pipeline

```text
HotKeyListener
  -> AudioCapture
  -> batch chunk planner in VoiceController
  -> WhisperTranscriber
  -> overlap text merger
  -> HallucinationGuard
  -> TextInjector
```

## Files

| File | Purpose |
| --- | --- |
| `VoiceController.cs` | Orchestrates PTT recording, batch transcription, merge, and final injection. |
| `AudioCapture.cs` | Captures microphone samples through NAudio. |
| `WhisperTranscriber.cs` | Whisper.net transcription path. |
| `WhisperModelCatalog.cs` | Supported selectable Whisper models. |
| `SherpaStreamingTranscriber.cs` | Alternate streaming transcription path. |
| `VoiceAudioChunkPlanner.cs` | Plans 35-second chunks with 2-second overlap. |
| `VoiceTextOverlapMerger.cs` | Removes duplicate text from overlapped chunks. |
| `VoiceTranscriptBuffer.cs` | Legacy helper for ordered text buffers. |
| `HallucinationGuard.cs` | Filters known silence hallucinations and risky commands. |
| `TextInjector.cs` | Sends final text to the selected destination. |

## Defaults

- Sample rate: `16000`.
- Whisper model directory: `%LOCALAPPDATA%\TinyBoss\models`.
- Whisper model id: `tiny.en` by default; selectable in settings.
- Push-to-talk key is configured in `tinyboss.json`.

## Rules

- Do not inject partial chunks while the PTT key is held.
- Flush on key-up, then inject once.
- If mic behavior breaks, check the selected NAudio device and whether another
  app has exclusive control of the device.
