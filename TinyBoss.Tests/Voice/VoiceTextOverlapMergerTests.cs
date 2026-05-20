using TinyBoss.Voice;
using Xunit;

namespace TinyBoss.Tests.Voice;

public class VoiceTextOverlapMergerTests
{
    [Fact]
    public void MergesExactWordOverlap()
    {
        var merged = VoiceTextOverlapMerger.Merge([
            "one two three four",
            "three four five six"
        ]);

        Assert.Equal("one two three four five six", merged);
    }

    [Fact]
    public void IgnoresPunctuationWhenFindingOverlap()
    {
        var merged = VoiceTextOverlapMerger.Merge([
            "this is the first part.",
            "First part, and the rest"
        ]);

        Assert.Equal("this is the first part. and the rest", merged);
    }

    [Fact]
    public void AppendsWhenThereIsNoOverlap()
    {
        var merged = VoiceTextOverlapMerger.Merge([
            "first idea",
            "second idea"
        ]);

        Assert.Equal("first idea second idea", merged);
    }
}
