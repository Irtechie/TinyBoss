using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Enumerates connected monitors with stable device names and friendly descriptions.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MonitorEnumerator
{
    public sealed record MonitorDesc(string DeviceName, string FriendlyName, int Width, int Height, bool IsPrimary);

    public static List<MonitorDesc> GetMonitors()
    {
        var monitors = new List<MonitorDesc>();

        EnumDisplayMonitors(nint.Zero, nint.Zero, (hMon, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                var deviceName = info.szDevice; // e.g. \\.\DISPLAY1
                bool isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                int w = info.rcMonitor.Right - info.rcMonitor.Left;
                int h = info.rcMonitor.Bottom - info.rcMonitor.Top;

                // Get friendly name from EnumDisplayDevices
                var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                string friendly = deviceName;
                if (EnumDisplayDevices(deviceName, 0, ref dd, 0))
                    friendly = string.IsNullOrWhiteSpace(dd.DeviceString) ? deviceName : dd.DeviceString;

                monitors.Add(new MonitorDesc(deviceName, friendly, w, h, isPrimary));
            }
            return true;
        }, nint.Zero);

        return monitors;
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
