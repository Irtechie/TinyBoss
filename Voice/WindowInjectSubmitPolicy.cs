namespace TinyBoss.Voice;

public static class WindowInjectSubmitPolicy
{
    public static bool ShouldSendExplicitEnter(bool isTerminal, bool usedClipboard)
    {
        return isTerminal || usedClipboard;
    }
}
