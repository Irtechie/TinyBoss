using TinyBoss.Voice;
using Xunit;

namespace TinyBoss.Tests.Voice;

public sealed class DictationClipboardPolicyTests
{
    [Fact]
    public void PasteFailureKeepsLatestDictationOnClipboard()
    {
        var decision = DictationClipboardPolicy.AfterPasteAttempt(pasteChordSucceeded: false);

        Assert.False(decision.RestorePreviousClipboard);
        Assert.True(decision.KeepLatestTranscriptForRetry);
        Assert.Contains("retained", decision.Message);
    }

    [Fact]
    public void UnverifiedPasteSuccessKeepsLatestDictationOnClipboard()
    {
        var decision = DictationClipboardPolicy.AfterPasteAttempt(pasteChordSucceeded: true);

        Assert.False(decision.RestorePreviousClipboard);
        Assert.True(decision.KeepLatestTranscriptForRetry);
        Assert.Contains("left on clipboard", decision.Message);
    }
}
