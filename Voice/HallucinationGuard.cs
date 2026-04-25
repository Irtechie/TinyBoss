using System.Text.RegularExpressions;

namespace TinyBoss.Voice;

/// <summary>
/// 4-layer defense against Whisper hallucinations and dangerous commands.
/// L1: Pre-inference audio quality check (RMS energy, duration)
/// L2: Whisper config (NoSpeechThreshold — applied at transcriber level)
/// L3: Post-inference segment validation (confidence, known phrases)
/// L4: Destructive command denylist with click-to-confirm
/// </summary>
public sealed class HallucinationGuard
{
    private const float MinRmsEnergy = 0.008f;
    private const float MinDurationSeconds = 0.5f;
    private const int MinSampleRate = 16000;

    // Known Whisper hallucination phrases (silence → these)
    private static readonly HashSet<string> KnownHallucinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "thanks for watching",
        "thank you for watching",
        "subscribe",
        "like and subscribe",
        "please subscribe",
        "thanks for listening",
        "thank you for listening",
        "you",
        "bye",
        "the end",
        "...",
        "♪",
        "music",
    };

    // Dangerous CLI patterns that require explicit confirmation
    private static readonly Regex[] DestructivePatterns =
    [
        new(@"\brm\s+(-[a-z]*r[a-z]*\s+|--recursive)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdel\s+/[a-z]*[sfq]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brmdir\s+/s", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bformat\s+[a-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bshutdown\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bcurl\b.*\|\s*(bash|sh|powershell|pwsh)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bpowershell\s+.*-enc", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdiskpart\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdd\s+if=", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(drop|truncate|delete\s+from)\s+\w", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>
    /// Layer 1: Pre-inference check. Returns true if audio is worth transcribing.
    /// </summary>
    public bool ShouldTranscribe(float[] samples)
    {
        if (samples.Length < MinSampleRate * MinDurationSeconds)
            return false; // Too short — likely accidental press

        float sumSquares = 0;
        for (int i = 0; i < samples.Length; i++)
            sumSquares += samples[i] * samples[i];

        float rms = MathF.Sqrt(sumSquares / samples.Length);
        return rms >= MinRmsEnergy;
    }

    /// <summary>
    /// Layer 3: Post-inference segment validation.
    /// Returns true if the transcription looks like real speech.
    /// </summary>
    public bool IsValidTranscription(string text, float noSpeechProb, float probability)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        if (trimmed.Length <= 2)
            return false;

        if (noSpeechProb > 0.4f)
            return false;

        if (probability < 0.3f)
            return false;

        if (KnownHallucinations.Contains(trimmed))
            return false;

        // Check if text is mostly non-alphabetic (noise transcription)
        int alphaCount = trimmed.Count(char.IsLetter);
        if (alphaCount < trimmed.Length * 0.3f)
            return false;

        return true;
    }

    /// <summary>
    /// Layer 4: Check if text contains dangerous CLI commands.
    /// Returns the matched pattern description if dangerous, null if safe.
    /// </summary>
    public string? CheckDestructiveCommand(string text)
    {
        foreach (var pattern in DestructivePatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
                return match.Value;
        }
        return null;
    }
}
