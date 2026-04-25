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
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    // Controls the flyout when hovering the maximize button
    private const string FlyoutValue = "EnableSnapAssistFlyout";
    // Controls the snap bar that appears when dragging to the top edge
    private const string SnapBarValue = "EnableSnapBar";

    /// <summary>
    /// Disables both the Windows 11 Snap Layouts flyout (maximize hover)
    /// and the Snap Bar (drag-to-top). Takes effect immediately.
    /// </summary>
    public static void DisableSnapLayouts()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(FlyoutValue, 0, RegistryValueKind.DWord);
            key.SetValue(SnapBarValue, 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Non-critical — Windows 10 or restricted permissions
        }
    }

    /// <summary>
    /// Re-enables Windows 11 Snap Layouts flyout and Snap Bar.
    /// </summary>
    public static void EnableSnapLayouts()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(FlyoutValue, 1, RegistryValueKind.DWord);
            key.SetValue(SnapBarValue, 1, RegistryValueKind.DWord);
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
