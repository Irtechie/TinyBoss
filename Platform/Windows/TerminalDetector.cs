using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Detects whether a window is a terminal by class name (primary) or process name (fallback).
/// Covers Windows Terminal (all variants), cmd/conhost, PowerShell, Git Bash.
/// VS Code terminal shares Electron's Chrome_WidgetWin_1 — NOT detectable separately.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TerminalDetector
{
    private static readonly HashSet<string> TerminalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASCADIA_HOSTING_WINDOW_CLASS",  // Windows Terminal (stable, preview, canary)
        "ConsoleWindowClass",             // cmd, PowerShell 5.1/7, conhost
        "mintty",                         // Git Bash
    };

    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "cmd", "powershell", "pwsh", "mintty",
    };

    /// <summary>
    /// Returns true if the HWND belongs to a terminal window.
    /// Checks: visible, not tool window, class name match, process name fallback.
    /// </summary>
    public static bool IsTerminalWindow(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        if (!TilingCoordinator.IsWindow(hwnd)) return false;
        if (!TilingCoordinator.IsWindowVisible(hwnd)) return false;

        // Reject tool windows (tooltips, popups, etc.)
        var exStyle = TilingCoordinator.GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

        // Primary: check window class name
        var sb = new StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) > 0)
        {
            if (TerminalClasses.Contains(sb.ToString())) return true;
        }

        // Fallback: check process name
        TilingCoordinator.GetWindowThreadProcessId(hwnd, out int pid);
        try
        {
            using var proc = Process.GetProcessById(pid);
            return TerminalProcesses.Contains(proc.ProcessName);
        }
        catch { return false; }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
}
