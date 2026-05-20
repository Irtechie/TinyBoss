using TinyBoss.Voice;
using Xunit;

namespace TinyBoss.Tests.Voice;

public sealed class ClipboardPasteTransportPolicyTests
{
    [Fact]
    public void TerminalTargetsUseRightClickPaste()
    {
        Assert.Equal(
            ClipboardPasteTransport.RightClick,
            ClipboardPasteTransportPolicy.ForTarget(isTerminal: true));
    }

    [Fact]
    public void NonTerminalTargetsUseCtrlV()
    {
        Assert.Equal(
            ClipboardPasteTransport.CtrlV,
            ClipboardPasteTransportPolicy.ForTarget(isTerminal: false));
    }
}
