using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyBoss.Core;

public sealed record ResumeSessionHistoryRecord(
    [property: JsonPropertyName("captured_at")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("monitor_handle")] string MonitorHandle,
    [property: JsonPropertyName("old_hwnd")] string OldHwnd,
    [property: JsonPropertyName("new_hwnd")] string? NewHwnd = null);

public static class ResumeSessionHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string StateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TinyBoss");

    public static string HistoryPath { get; } = Path.Combine(StateDirectory, "resume-history.jsonl");

    public static void Append(ResumeSessionHistoryRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Command))
            return;

        try
        {
            Directory.CreateDirectory(StateDirectory);
            File.AppendAllText(
                HistoryPath,
                JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        }
        catch
        {
            // Resume history is useful, but it must never block the bossify flow.
        }
    }

    public static IReadOnlyList<ResumeSessionHistoryRecord> ReadLatest(int max = 40)
    {
        if (!File.Exists(HistoryPath))
            return [];

        try
        {
            return File.ReadAllLines(HistoryPath)
                .Reverse()
                .Select(TryParse)
                .Where(record => record is not null)
                .Cast<ResumeSessionHistoryRecord>()
                .Take(max)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static string FormatLatest(int max = 40)
    {
        var records = ReadLatest(max);
        var builder = new StringBuilder();
        builder.AppendLine($"History file: {HistoryPath}");
        builder.AppendLine();

        if (records.Count == 0)
        {
            builder.AppendLine("No resume sessions captured yet.");
            return builder.ToString();
        }

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            builder.AppendLine($"{i + 1}. {record.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} | {record.Name} | {record.Tool ?? "cli"}");
            builder.AppendLine($"   cwd: {record.Cwd ?? "(unknown)"}");
            builder.AppendLine($"   command: {record.Command}");
            builder.AppendLine($"   slot: {record.Slot} monitor: {record.MonitorHandle}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string? InferTool(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var text = command.Trim().ToLowerInvariant();
        if (text.StartsWith("copilot ")) return "copilot";
        if (text.StartsWith("ghcp ")) return "copilot";
        if (text.StartsWith("codex ")) return "codex";
        if (text.StartsWith("claude ")) return "claude";
        if (text.StartsWith("inferno ")) return "inferno";
        if (text.StartsWith("qwen ")) return "qwen";
        if (text.StartsWith("npx ")) return "npx";
        if (text.StartsWith("uvx ")) return "uvx";
        return null;
    }

    private static ResumeSessionHistoryRecord? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ResumeSessionHistoryRecord>(
                line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
