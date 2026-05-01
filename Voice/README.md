# TinyBoss Voice

Local push-to-talk speech-to-text pipeline.

TinyBoss voice is local. It captures microphone audio, segments speech with VAD,
transcribes with Whisper, filters hallucinations/dangerous text, then injects a
single final text payload.

## Pipeline

```text
HotKeyListener
  -> AudioCapture
  -> VAD in VoiceController
  -> WhisperTranscriber
  -> HallucinationGuard
  -> TextInjector
```

## Files

| File | Purpose |
| --- | --- |
| `VoiceController.cs` | Orchestrates PTT, VAD, chunk queue, transcript buffer, and final injection. |
| `AudioCapture.cs` | Captures microphone samples through NAudio. |
| `WhisperTranscriber.cs` | Whisper.net transcription path. |
| `SherpaStreamingTranscriber.cs` | Alternate streaming transcription path. |
| `VoiceTranscriptBuffer.cs` | Orders and combines transcription chunks. |
| `HallucinationGuard.cs` | Filters known silence hallucinations and risky commands. |
| `TextInjector.cs` | Sends final text to the selected destination. |

## Defaults

- Sample rate: `16000`.
- Whisper model directory: `%LOCALAPPDATA%\TinyBoss\models`.
- Push-to-talk key is configured in `tinyboss.json`.

## Rules

- Do not inject partial chunks while the PTT key is held.
- Flush on key-up, then inject once.
- Keep stale transcription results isolated by session token.
- If mic behavior breaks, check the selected NAudio device and whether another
  app has exclusive control of the device.
