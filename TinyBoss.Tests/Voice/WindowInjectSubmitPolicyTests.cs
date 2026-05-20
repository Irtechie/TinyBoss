using TinyBoss.Voice;
using Xunit;

namespace TinyBoss.Tests.Voice;

public sealed class WindowInjectSubmitPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void ExplicitEnterIsSentForTerminalOrClipboardWindowInject(bool isTerminal, bool usedClipboard, bool expected)
    {
        Assert.Equal(expected, WindowInjectSubmitPolicy.ShouldSendExplicitEnter(isTerminal, usedClipboard));
    }
}
