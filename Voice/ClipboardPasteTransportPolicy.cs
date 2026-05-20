namespace TinyBoss.Voice;

public enum ClipboardPasteTransport
{
    CtrlV,
    RightClick
}

public static class ClipboardPasteTransportPolicy
{
    public static ClipboardPasteTransport ForTarget(bool isTerminal)
    {
        return isTerminal
            ? ClipboardPasteTransport.RightClick
            : ClipboardPasteTransport.CtrlV;
    }
}
