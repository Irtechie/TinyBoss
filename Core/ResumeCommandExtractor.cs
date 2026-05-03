namespace TinyBoss.Core;

public static class ResumeCommandExtractor
{
    private static readonly System.Text.RegularExpressions.Regex[] CommandPatterns =
    [
        new(@"\b(?:copilot|ghcp)\s+--resume=(?:""[^""]+""|'[^']+'|[^\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"\b(?:codex|inferno|claude|qwen|ghcp)\s+resume\s+[A-Za-z0-9._:-]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)
    ];

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

        foreach (var pattern in CommandPatterns)
        {
            var match = pattern.Match(normalized);
            if (match.Success)
                return match.Value.Trim().Trim('`', '.', ';', '?');
        }

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
