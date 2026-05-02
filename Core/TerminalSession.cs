using System.Diagnostics;
namespace TinyBoss.Core;

public interface ITerminalSessionBackend : IAsyncDisposable
{
    Process Process { get; }
    bool RawStreamAvailable { get; }

    IAsyncEnumerable<string> ReadOutputAsync(CancellationToken ct);
    Task WriteInputAsync(ReadOnlyMemory<char> text, CancellationToken ct);
    Task WriteSignalAsync(string signal, CancellationToken ct);
    Task ResizeAsync(short columns, short rows, CancellationToken ct);
    void Kill();
}

public sealed class TerminalTranscriptBuffer
{
    private const int DefaultLineLimit = 200;
    private static readonly System.Text.RegularExpressions.Regex AnsiRegex = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly int _lineLimit;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private string _partial = string.Empty;
    private int _droppedLineCount;

    public TerminalTranscriptBuffer(int lineLimit = DefaultLineLimit)
    {
        _lineLimit = Math.Max(1, lineLimit);
    }

    public void AddChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        var cleaned = AnsiRegex.Replace(chunk, string.Empty).Replace("\r\n", "\n");
        lock (_gate)
        {
            var segments = cleaned.Split('\n');
            for (var i = 0; i < segments.Length; i++)
            {
                ApplyCarriageReturns(segments[i]);
                if (i < segments.Length - 1)
                    CommitPartial();
            }

            Trim();
        }
    }

    public string[] Tail(int maxLines)
    {
        lock (_gate)
        {
            var requested = Math.Max(1, maxLines);
            var tail = _lines.ToArray();
            if (!string.IsNullOrWhiteSpace(_partial))
                tail = [.. tail, _partial];

            var rows = tail.TakeLast(requested).ToList();
            if (_droppedLineCount <= 0)
                return rows.ToArray();

            var marker = $"[TinyBoss: {_droppedLineCount} earlier transcript line(s) dropped]";
            if (requested == 1)
                return [marker];

            if (rows.Count >= requested)
                rows = rows.TakeLast(requested - 1).ToList();
            rows.Insert(0, marker);
            return rows.ToArray();
        }
    }

    private void ApplyCarriageReturns(string text)
    {
        foreach (var part in text.Split('\r'))
        {
            _partial = part;
        }
    }

    private void CommitPartial()
    {
        if (!string.IsNullOrWhiteSpace(_partial))
            _lines.Enqueue(_partial);
        _partial = string.Empty;
    }

    private void Trim()
    {
        while (_lines.Count > _lineLimit)
        {
            _lines.Dequeue();
            _droppedLineCount++;
        }
    }
}
