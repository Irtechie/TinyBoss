namespace TinyBoss.Voice;

public sealed record VoiceDictationResult(
    bool Success,
    string Text,
    string Message,
    int Characters)
{
    public static VoiceDictationResult Ok(string text, string message) =>
        new(true, text, message, text.Length);

    public static VoiceDictationResult Failed(string message) =>
        new(false, string.Empty, message, 0);
}
