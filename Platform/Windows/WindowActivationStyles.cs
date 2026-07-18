using Avalonia.Controls;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TinyBoss.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WindowActivationStyles
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public static bool MakeNonActivating(Window window, bool clickThrough = false)
    {
        if (window.TryGetPlatformHandle() is not { } handle)
            return false;

        var hwnd = handle.Handle;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (clickThrough)
            exStyle |= WS_EX_TRANSPARENT;

        SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle);
        return true;
    }

    public static bool SetClickThrough(Window window, bool enabled)
    {
        if (window.TryGetPlatformHandle() is not { } handle)
            return false;

        var hwnd = handle.Handle;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        exStyle = enabled
            ? exStyle | WS_EX_TRANSPARENT
            : exStyle & ~WS_EX_TRANSPARENT;

        SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle);
        return true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
