using System.Runtime.Versioning;
using System.Windows.Automation;

namespace TinyBoss.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WindowTextCapture
{
    private const int MaxCharacters = 24000;
    private const int MaxLines = 80;

    public static string[] CaptureTail(nint hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
                return [];

            if (TryCapture(root, out var text))
                return ToTail(text);

            var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            for (var i = 0; i < descendants.Count; i++)
            {
                if (TryCapture(descendants[i], out text))
                    return ToTail(text);
            }
        }
        catch
        {
            // Some elevated or custom-rendered windows do not expose UIA text.
        }

        return [];
    }

    private static bool TryCapture(AutomationElement element, out string text)
    {
        text = string.Empty;
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) ||
            pattern is not TextPattern textPattern)
        {
            return false;
        }

        text = textPattern.DocumentRange.GetText(MaxCharacters);
        return !string.IsNullOrWhiteSpace(text);
    }

    private static string[] ToTail(string text)
    {
        return text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(MaxLines)
            .ToArray();
    }
}
