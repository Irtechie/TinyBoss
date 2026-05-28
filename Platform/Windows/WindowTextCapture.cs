using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Automation;

namespace TinyBoss.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WindowTextCapture
{
    private const int MaxCharacters = 24000;
    private const int MaxLines = 80;
    private const int ErrorAccessDenied = 5;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    public static string[] CaptureTail(nint hwnd)
    {
        if (TryCaptureClassicConsoleTail(hwnd, out var consoleTail))
            return consoleTail;

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
                return [];

            var windowTitle = TilingCoordinator.GetWindowTitle(hwnd);

            var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            for (var i = 0; i < descendants.Count; i++)
            {
                if (TryCapture(descendants[i], out var text) &&
                    IsUsefulCapturedText(text, windowTitle))
                {
                    return ToTail(text);
                }
            }

            if (TryCapture(root, out var rootText) &&
                IsUsefulCapturedText(rootText, windowTitle))
            {
                return ToTail(rootText);
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

    internal static bool IsUsefulCapturedText(string text, string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalizedText = NormalizeForComparison(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        var normalizedTitle = NormalizeForComparison(windowTitle ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (normalizedText.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase))
            return false;

        var lines = ToTail(text);
        if (lines.Length == 0)
            return false;

        return lines.Any(line =>
            !NormalizeForComparison(line).Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryCaptureClassicConsoleTail(nint hwnd, out string[] tail)
    {
        tail = [];

        if (!IsClassicConsoleWindow(hwnd))
            return false;

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
            return false;

        var attached = AttachConsole(processId);
        if (!attached && Marshal.GetLastWin32Error() == ErrorAccessDenied)
        {
            FreeConsole();
            attached = AttachConsole(processId);
        }

        if (!attached)
            return false;

        try
        {
            using var output = CreateFile(
                "CONOUT$",
                GenericRead,
                FileShareRead | FileShareWrite,
                nint.Zero,
                OpenExisting,
                0,
                nint.Zero);

            if (output.IsInvalid ||
                !GetConsoleScreenBufferInfo(output, out var info))
            {
                return false;
            }

            var width = info.Window.Right - info.Window.Left + 1;
            var height = info.Window.Bottom - info.Window.Top + 1;
            if (width <= 0 || height <= 0)
                return false;

            var lines = new List<string>(height);
            for (var y = info.Window.Top; y <= info.Window.Bottom; y++)
            {
                var buffer = new char[width];
                if (!ReadConsoleOutputCharacter(output, buffer, (uint)width, new COORD(info.Window.Left, y), out var read) ||
                    read == 0)
                {
                    continue;
                }

                var line = new string(buffer, 0, (int)Math.Min(read, (uint)buffer.Length)).TrimEnd();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }

            if (lines.Count == 0)
                return false;

            tail = lines.TakeLast(MaxLines).ToArray();
            return true;
        }
        finally
        {
            FreeConsole();
        }
    }

    private static bool IsClassicConsoleWindow(nint hwnd)
    {
        if (hwnd == nint.Zero)
            return false;

        var classBuilder = new StringBuilder(256);
        if (GetClassName(hwnd, classBuilder, classBuilder.Capacity) <= 0)
            return false;

        return classBuilder.ToString().Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForComparison(string text) =>
        string.Join(
            ' ',
            text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Trim();

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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct COORD(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD Size;
        public COORD CursorPosition;
        public ushort Attributes;
        public SMALL_RECT Window;
        public COORD MaximumWindowSize;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(
        SafeFileHandle hConsoleOutput,
        out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleOutputCharacterW")]
    private static extern bool ReadConsoleOutputCharacter(
        SafeFileHandle hConsoleOutput,
        [Out] char[] lpCharacter,
        uint nLength,
        COORD dwReadCoord,
        out uint lpNumberOfCharsRead);
}
