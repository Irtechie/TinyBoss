namespace TinyBoss.Voice;

public static class DictationClipboardPolicy
{
    public static DictationClipboardDecision AfterPasteAttempt(bool pasteChordSucceeded)
    {
        return pasteChordSucceeded
            ? new DictationClipboardDecision(
                RestorePreviousClipboard: false,
                KeepLatestTranscriptForRetry: true,
                Message: "clipboard/ctrl+v (latest dictation left on clipboard for retry)")
            : new DictationClipboardDecision(
                RestorePreviousClipboard: false,
                KeepLatestTranscriptForRetry: true,
                Message: "paste chord failed; latest dictation retained on clipboard for retry");
    }
}

public sealed record DictationClipboardDecision(
    bool RestorePreviousClipboard,
    bool KeepLatestTranscriptForRetry,
    string Message);
