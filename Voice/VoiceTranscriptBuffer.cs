namespace TinyBoss.Voice;

public sealed class VoiceTranscriptBuffer
{
    private readonly List<string> _segments = new();
    private readonly object _lock = new();

    public int SegmentCount
    {
        get
        {
            lock (_lock)
                return _segments.Count;
        }
    }

    public int CharacterCount
    {
        get
        {
            lock (_lock)
                return _segments.Sum(s => s.Length);
        }
    }

    public bool Add(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        lock (_lock)
            _segments.Add(trimmed);

        return true;
    }

    public void Clear()
    {
        lock (_lock)
            _segments.Clear();
    }

    public string Flush()
    {
        lock (_lock)
        {
            var text = string.Join(" ", _segments).Trim();
            _segments.Clear();
            return text;
        }
    }
}
