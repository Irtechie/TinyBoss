using TinyBoss.Voice;
using Xunit;

namespace TinyBoss.Tests.Voice;

public sealed class VoiceTranscriptBufferTests
{
    [Fact]
    public void FlushJoinsTrimmedSegmentsInOrder()
    {
        var buffer = new VoiceTranscriptBuffer();

        Assert.True(buffer.Add("  first chunk  "));
        Assert.True(buffer.Add("second chunk"));
        Assert.True(buffer.Add(" third chunk "));

        Assert.Equal(3, buffer.SegmentCount);
        Assert.Equal("first chunk second chunk third chunk", buffer.Flush());
    }

    [Fact]
    public void AddIgnoresWhitespaceOnlySegments()
    {
        var buffer = new VoiceTranscriptBuffer();

        Assert.False(buffer.Add("   "));
        Assert.False(buffer.Add("\r\n\t"));

        Assert.Equal(0, buffer.SegmentCount);
        Assert.Equal(string.Empty, buffer.Flush());
    }

    [Fact]
    public void FlushClearsBufferedTranscript()
    {
        var buffer = new VoiceTranscriptBuffer();
        buffer.Add("one");
        buffer.Add("two");

        Assert.Equal("one two", buffer.Flush());
        Assert.Equal(0, buffer.SegmentCount);
        Assert.Equal(string.Empty, buffer.Flush());
    }

    [Fact]
    public void ClearDropsPendingTranscript()
    {
        var buffer = new VoiceTranscriptBuffer();
        buffer.Add("one");
        buffer.Add("two");

        buffer.Clear();

        Assert.Equal(0, buffer.SegmentCount);
        Assert.Equal(0, buffer.CharacterCount);
        Assert.Equal(string.Empty, buffer.Flush());
    }
}
