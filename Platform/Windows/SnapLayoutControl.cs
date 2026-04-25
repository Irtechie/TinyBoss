using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Controls the Windows 11 Snap Layouts flyout via registry.
/// When disabled, TinyBoss intercepts drag-to-top-edge instead.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SnapLayoutControl
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "EnableSnapAssistFlyout";

    /// <summary>
    /// Disables the Windows 11 Snap Layouts flyout that appears
    /// when dragging a window to the top edge of the screen.
    /// Takes effect immediately — no restart needed.
    /// </summary>
    public static void DisableSnapLayouts()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Non-critical — Windows 10 or restricted permissions
        }
    }

    /// <summary>
    /// Re-enables the Windows 11 Snap Layouts flyout.
    /// </summary>
    public static void EnableSnapLayouts()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(ValueName, 1, RegistryValueKind.DWord);
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    /// Applies the current config setting.
    /// </summary>
    public static void Apply(bool overrideSnapLayouts)
    {
        if (overrideSnapLayouts)
            DisableSnapLayouts();
        else
            EnableSnapLayouts();
    }
}
