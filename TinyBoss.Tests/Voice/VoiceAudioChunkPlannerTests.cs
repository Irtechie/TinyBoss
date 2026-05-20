using TinyBoss.Voice;
using Xunit;

namespace TinyBoss.Tests.Voice;

public class VoiceAudioChunkPlannerTests
{
    [Fact]
    public void ShortRecordingUsesSingleChunk()
    {
        var chunks = VoiceAudioChunkPlanner.Plan(VoiceAudioChunkPlanner.SampleRate * 10);

        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.Start);
        Assert.Equal(VoiceAudioChunkPlanner.SampleRate * 10, chunk.Length);
    }

    [Fact]
    public void LongRecordingUsesOverlappedChunks()
    {
        var chunks = VoiceAudioChunkPlanner.Plan(
            VoiceAudioChunkPlanner.SampleRate * 60,
            chunkSeconds: 35,
            overlapSeconds: 2.0f);

        Assert.True(chunks.Count > 1);
        for (var i = 1; i < chunks.Count; i++)
        {
            var overlap = chunks[i - 1].End - chunks[i].Start;
            Assert.Equal(VoiceAudioChunkPlanner.SampleRate * 2, overlap);
        }
    }

    [Fact]
    public void TinyRemainderIsNotEmittedAsOwnChunk()
    {
        var sampleRate = VoiceAudioChunkPlanner.SampleRate;
        var chunks = VoiceAudioChunkPlanner.Plan(
            sampleRate * 24 + sampleRate,
            chunkSeconds: 24,
            overlapSeconds: 1.5f,
            minChunkSeconds: 2);

        Assert.Equal(2, chunks.Count);
        Assert.True(chunks[^1].Length >= sampleRate * 2);
    }
}
