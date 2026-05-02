namespace TinyBoss.Core;

public static class ResumeCommandExtractor
{
    private static readonly string[] Prefixes =
    [
        "copilot ",
        "ghcp ",
        "codex ",
        "inferno ",
        "claude ",
        "qwen ",
        "npx ",
        "uvx "
    ];

    public static string? Find(IEnumerable<string> tail)
    {
        foreach (var line in tail.Reverse())
        {
            var command = Extract(line);
            if (!string.IsNullOrWhiteSpace(command))
                return command;
        }

        return null;
    }

    public static string? Extract(string line)
    {
        var normalized = line
            .Trim()
            .TrimStart('>', '$', '`', '"', '\'', ' ');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var lower = normalized.ToLowerInvariant();
        if (!lower.Contains("resume"))
            return null;

        foreach (var prefix in Prefixes)
        {
            var index = lower.IndexOf(prefix, StringComparison.Ordinal);
            if (index < 0)
                continue;

            var command = normalized[index..]
                .Trim()
                .Trim('`', '.', ';');
            return command.Contains("resume", StringComparison.OrdinalIgnoreCase)
                ? command
                : null;
        }

        return null;
    }
}
