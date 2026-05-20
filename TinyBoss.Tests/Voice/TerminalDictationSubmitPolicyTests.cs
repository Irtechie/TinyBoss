using TinyBoss.Voice;
using Xunit;

namespace TinyBoss.Tests.Voice;

public sealed class TerminalDictationSubmitPolicyTests
{
    [Theory]
    [InlineData("hello", true, "hello\n")]
    [InlineData("hello ", true, "hello\n")]
    [InlineData("hello\n", true, "hello\n")]
    [InlineData("hello", false, "hello")]
    public void TerminalTargetsEndWithNewline(string text, bool isTerminal, string expected)
    {
        Assert.Equal(expected, TerminalDictationSubmitPolicy.PrepareText(text, isTerminal));
    }
}
