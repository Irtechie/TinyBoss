using System.Text.RegularExpressions;

namespace TinyBoss.Voice;

public static class VoiceTextOverlapMerger
{
    private const int MaxOverlapWords = 16;

    public static string Merge(IReadOnlyList<string> segments)
    {
        var mergedWords = new List<string>();

        foreach (var segment in segments)
        {
            var words = SplitWords(segment);
            if (words.Count == 0)
                continue;

            var overlap = FindOverlap(mergedWords, words);
            mergedWords.AddRange(words.Skip(overlap));
        }

        return string.Join(' ', mergedWords).Trim();
    }

    private static IReadOnlyList<string> SplitWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int FindOverlap(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var max = Math.Min(Math.Min(left.Count, right.Count), MaxOverlapWords);
        for (var size = max; size >= 1; size--)
        {
            var match = true;
            for (var i = 0; i < size; i++)
            {
                if (!SameWord(left[left.Count - size + i], right[i]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return size;
        }

        return 0;
    }

    private static bool SameWord(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string word) =>
        Regex.Replace(word, @"[^\p{L}\p{N}]+", "");
}
