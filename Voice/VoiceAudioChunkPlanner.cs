namespace TinyBoss.Voice;

public static class VoiceAudioChunkPlanner
{
    public const int SampleRate = 16000;
    public const float DefaultChunkSeconds = 35.0f;
    public const float DefaultOverlapSeconds = 2.0f;
    public const float MinChunkSeconds = 0.5f;

    public static IReadOnlyList<VoiceAudioChunk> Plan(
        int sampleCount,
        int sampleRate = SampleRate,
        float chunkSeconds = DefaultChunkSeconds,
        float overlapSeconds = DefaultOverlapSeconds,
        float minChunkSeconds = MinChunkSeconds)
    {
        if (sampleCount <= 0)
            return [];

        var chunkSamples = Math.Max(1, (int)(sampleRate * chunkSeconds));
        var overlapSamples = Math.Clamp((int)(sampleRate * overlapSeconds), 0, chunkSamples - 1);
        var minChunkSamples = Math.Max(1, (int)(sampleRate * minChunkSeconds));

        if (sampleCount <= chunkSamples)
            return [new VoiceAudioChunk(0, sampleCount)];

        var chunks = new List<VoiceAudioChunk>();
        var start = 0;
        while (start < sampleCount)
        {
            var remaining = sampleCount - start;
            if (remaining < minChunkSamples && chunks.Count > 0)
                break;

            var length = Math.Min(chunkSamples, remaining);
            chunks.Add(new VoiceAudioChunk(start, length));

            if (start + length >= sampleCount)
                break;

            start = start + length - overlapSamples;
        }

        return chunks;
    }
}

public readonly record struct VoiceAudioChunk(int Start, int Length)
{
    public int End => Start + Length;
}
