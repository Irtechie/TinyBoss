using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Controls Windows 11 Snap Layouts and Snap Bar via registry.
/// When disabled, TinyBoss intercepts drag-to-top-edge instead.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SnapLayoutControl
{
    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string DesktopPath = @"Control Panel\Desktop";

    // Controls the flyout when hovering the maximize button
    private const string FlyoutValue = "EnableSnapAssistFlyout";
    // Controls the snap bar that appears when dragging to the top edge
    private const string SnapBarValue = "EnableSnapBar";
    // DITest controls the drag-to-top snap layout panel (Win11 23H2+)
    private const string DITestValue = "DITest";

    /// <summary>
    /// Disables Windows 11 Snap Layouts flyout (maximize hover),
    /// Snap Bar (drag-to-top), and DITest (newer drag-to-top panel).
    /// Optionally restarts Explorer.exe to apply immediately.
    /// </summary>
    public static void DisableSnapLayouts(bool restartExplorer = false)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvanced);
            key.SetValue(FlyoutValue, 0, RegistryValueKind.DWord);
            key.SetValue(SnapBarValue, 0, RegistryValueKind.DWord);
            key.SetValue(DITestValue, 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Non-critical — Windows 10 or restricted permissions
        }

        if (restartExplorer)
            RestartExplorer();
    }

    /// <summary>
    /// Re-enables Windows 11 Snap Layouts flyout, Snap Bar, and DITest.
    /// </summary>
    public static void EnableSnapLayouts(bool restartExplorer = false)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvanced);
            key.SetValue(FlyoutValue, 1, RegistryValueKind.DWord);
            key.SetValue(SnapBarValue, 1, RegistryValueKind.DWord);
            // Remove DITest to restore default behavior
            key.DeleteValue(DITestValue, throwOnMissingValue: false);
        }
        catch
        {
            // Non-critical
        }

        if (restartExplorer)
            RestartExplorer();
    }

    /// <summary>
    /// Applies the current config setting.
    /// </summary>
    public static void Apply(bool overrideSnapLayouts)
    {
        if (overrideSnapLayouts)
            DisableSnapLayouts(restartExplorer: false);
        else
            EnableSnapLayouts(restartExplorer: false);
    }

    /// <summary>
    /// Restarts Explorer.exe so registry changes take effect immediately.
    /// </summary>
    private static void RestartExplorer()
    {
        try
        {
            // Signal Explorer to exit gracefully
            var trayHwnd = FindWindow("Shell_TrayWnd", null);
            if (trayHwnd != nint.Zero)
                PostMessage(trayHwnd, 0x5B4, nint.Zero, nint.Zero); // WM_USER+436 = graceful exit

            // Give it a moment to close
            Thread.Sleep(1500);

            // Start Explorer again
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            });
        }
        catch
        {
            // If graceful restart fails, try starting explorer anyway
            try { Process.Start("explorer.exe"); } catch { }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);
}
