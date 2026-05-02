using TinyBoss.Core;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class TerminalTranscriptBufferTests
{
    [Fact]
    public void AddChunk_StripsAnsiSequences()
    {
        var buffer = new TerminalTranscriptBuffer();

        buffer.AddChunk("\u001b[31merror\u001b[0m\n");

        Assert.Equal(["error"], buffer.Tail(10));
    }

    [Fact]
    public void AddChunk_TreatsCarriageReturnAsPromptReplacement()
    {
        var buffer = new TerminalTranscriptBuffer();

        buffer.AddChunk("Downloading 10%\rDownloading 90%\nDone\n");

        Assert.Equal(["Downloading 90%", "Done"], buffer.Tail(10));
    }

    [Fact]
    public void Tail_KeepsBoundedRecentLines()
    {
        var buffer = new TerminalTranscriptBuffer(lineLimit: 2);

        buffer.AddChunk("one\ntwo\nthree\n");

        Assert.Equal(["[TinyBoss: 1 earlier transcript line(s) dropped]", "two", "three"], buffer.Tail(10));
    }

    [Fact]
    public void Tail_PreservesDropMarkerWhenTailLimitIsSmall()
    {
        var buffer = new TerminalTranscriptBuffer(lineLimit: 2);

        buffer.AddChunk("one\ntwo\nthree\n");

        Assert.Equal(["[TinyBoss: 1 earlier transcript line(s) dropped]", "three"], buffer.Tail(2));
    }
}
