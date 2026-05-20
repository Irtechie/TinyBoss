namespace TinyBoss.Voice;

public static class TerminalDictationSubmitPolicy
{
    public static string PrepareText(string text, bool isTerminal)
    {
        if (!isTerminal)
            return text;

        var trimmed = text.TrimEnd();
        return trimmed.EndsWith('\n') || trimmed.EndsWith('\r')
            ? trimmed
            : trimmed + "\n";
    }
}
