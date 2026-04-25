using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Slot numbering for each grid layout:
/// 2-pane: [0=Left, 1=Right]
/// 4-pane: [0=TopLeft, 1=TopRight, 2=BottomLeft, 3=BottomRight]
/// 6-pane: [0=TopLeft, 1=TopCenter, 2=TopRight, 3=BottomLeft, 4=BottomCenter, 5=BottomRight]
/// </summary>
public sealed record TileSlot(nint Hwnd, int ProcessId, string? SessionId, string? Alias = null);

/// <summary>
/// Coordinates all tiling state and Win32 window positioning.
/// All mutations are serialized through a lock to prevent race conditions.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TilingCoordinator : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, TileSlot> _slots = new();
    private readonly Dictionary<nint, RegisteredWaitHandle> _processWaits = new();
    private readonly ILogger<TilingCoordinator> _logger;
    private nint _activeMonitor;
    private bool _disposed;
    private Timer? _reflowDebounce;
    private bool _dragInProgress;
    private bool _reflowPending;
    private const int REFLOW_DEBOUNCE_MS = 100;

    /// <summary>Fires after any slot mutation. Subscribers must marshal to UI thread.</summary>
    public event Action? SlotsChanged;

    /// <summary>Grid layout for 6-pane: "2x3" (2 rows × 3 cols) or "3x2" (3 rows × 2 cols).</summary>
    public string Layout { get; set; } = "2x3";

    public TilingCoordinator(ILogger<TilingCoordinator> logger)
    {
        _logger = logger;
    }

    /// <summary>Grid size computed from occupied window count — no manual setter.</summary>
    public int GridSize
    {
        get
        {
            lock (_lock)
                return OccupiedCount switch
                {
                    0 or 1 => 1,
                    2 => 2,
                    3 or 4 => 4,
                    _ => 6,
                };
        }
    }

    /// <summary>Grid size that includes room for one more window (used during drag).</summary>
    public int GridSizeForNextWindow
    {
        get
        {
            lock (_lock)
                return (OccupiedCount + 1) switch
                {
                    0 or 1 => 1,
                    2 => 2,
                    3 or 4 => 4,
                    _ => 6,
                };
        }
    }

    // ── Slot queries (thread-safe) ───────────────────────────────────────────

    public IReadOnlyDictionary<int, TileSlot> GetSnapshot()
    {
        lock (_lock) return new Dictionary<int, TileSlot>(_slots);
    }

    public int OccupiedCount
    {
        get { lock (_lock) return _slots.Count; }
    }

    public bool IsSlotOccupied(int slot)
    {
        lock (_lock) return _slots.ContainsKey(slot);
    }

    /// <summary>Find which slot a given HWND occupies. Returns -1 if not tiled.</summary>
    public int FindSlotForHwnd(nint hwnd)
    {
        lock (_lock)
        {
            foreach (var kv in _slots)
                if (kv.Value.Hwnd == hwnd) return kv.Key;
            return -1;
        }
    }

    // ── Core mutations (serialized) ──────────────────────────────────────────

    /// <summary>
    /// Assign a window to a grid slot. Validates HWND before assignment.
    /// Returns true if assignment succeeded.
    /// </summary>
    public bool AssignToSlot(int slot, nint hwnd, string? sessionId = null)
    {
        lock (_lock)
        {
            if (!IsWindow(hwnd))
            {
                _logger.LogWarning("KH: Cannot tile invalid HWND {Hwnd}", hwnd);
                return false;
            }

            // Capacity check: max 6 windows
            if (_slots.Count >= 6 && !_slots.ContainsKey(slot) && !_slots.Any(kv => kv.Value.Hwnd == hwnd))
                return false;

            // If this HWND is already in another slot, remove it first
            RemoveHwndLocked(hwnd);

            // If slot is occupied, evict the current occupant
            if (_slots.ContainsKey(slot))
                RemoveSlotLocked(slot);

            GetWindowThreadProcessId(hwnd, out int pid);
            var tileSlot = new TileSlot(hwnd, pid, sessionId);
            _slots[slot] = tileSlot;

            // Register for process exit cleanup
            RegisterProcessExitLocked(hwnd, pid);

            _logger.LogInformation("KH: Tiled HWND {Hwnd} (PID {Pid}) to slot {Slot}", hwnd, pid, slot);
        }

        SlotsChanged?.Invoke();
        return true;
    }

    /// <summary>Remove a specific HWND from whatever slot it occupies.</summary>
    public bool RemoveWindow(nint hwnd)
    {
        bool removed;
        lock (_lock)
        {
            removed = RemoveHwndLocked(hwnd);
        }
        if (removed) SlotsChanged?.Invoke();
        return removed;
    }

    /// <summary>Clear all slots.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            foreach (var wait in _processWaits.Values)
                wait.Unregister(null);
            _processWaits.Clear();
            _slots.Clear();
        }
        SlotsChanged?.Invoke();
    }

    /// <summary>
    /// Remove any slots whose HWND is no longer valid (window closed/destroyed).
    /// Returns true if any slots were pruned.
    /// </summary>
    public bool PruneDeadWindows()
    {
        bool pruned = false;
        lock (_lock)
        {
            var dead = _slots.Where(kv => !IsWindow(kv.Value.Hwnd)).Select(kv => kv.Key).ToList();
            foreach (var slot in dead)
            {
                RemoveSlotLocked(slot);
                pruned = true;
            }
        }
        if (pruned) SlotsChanged?.Invoke();
        return pruned;
    }

    // ── Drag-aware reflow ────────────────────────────────────────────────────

    public void OnDragStarted() => _dragInProgress = true;

    public void OnDragEnded(nint monitorHandle)
    {
        _dragInProgress = false;
        if (_reflowPending)
        {
            _reflowPending = false;
            ScheduleReflow(monitorHandle);
        }
    }

    /// <summary>Schedule a debounced rebalance + reposition on the given monitor.</summary>
    public void ScheduleReflow(nint monitorHandle)
    {
        if (_dragInProgress)
        {
            _reflowPending = true;
            return;
        }
        _reflowDebounce?.Dispose();
        _reflowDebounce = new Timer(_ =>
        {
            Rebalance(monitorHandle);
        }, null, REFLOW_DEBOUNCE_MS, Timeout.Infinite);
    }

    /// <summary>
    /// Rename a tiled window — sets internal alias and calls SetWindowText (one-shot courtesy).
    /// </summary>
    public bool RenameSlot(int slot, string alias)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slot, out var ts)) return false;
            if (!IsWindow(ts.Hwnd)) { RemoveSlotLocked(slot); return false; }
            _slots[slot] = ts with { Alias = alias };
            SetWindowText(ts.Hwnd, alias); // terminals overwrite in 0-5ms; alias is source of truth
        }
        SlotsChanged?.Invoke();
        return true;
    }

    /// <summary>Find the first empty slot in the current grid. Returns -1 if full.</summary>
    public int FindNextEmptySlot()
    {
        lock (_lock)
        {
            int max = GridSize;
            for (int i = 0; i < max; i++)
                if (!_slots.ContainsKey(i)) return i;
            return -1;
        }
    }

    /// <summary>
    /// Rebalance: compact occupied windows into contiguous slots and reposition.
    /// </summary>
    public void Rebalance(nint monitorHandle)
    {
        lock (_lock)
        {
            // Validate all HWNDs first, remove dead ones
            var deadSlots = _slots.Where(kv => !IsWindow(kv.Value.Hwnd)).Select(kv => kv.Key).ToList();
            foreach (var slot in deadSlots)
                RemoveSlotLocked(slot);

            // Compact into contiguous slots
            var occupied = _slots.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            _slots.Clear();
            for (int i = 0; i < occupied.Count; i++)
                _slots[i] = occupied[i];

            // Reposition all
            PositionAllLocked(monitorHandle);
        }
        SlotsChanged?.Invoke();
    }

    // ── Win32 window positioning ─────────────────────────────────────────────

    /// <summary>
    /// Position a single window to its assigned slot on the given monitor.
    /// </summary>
    public void PositionWindow(int slot, nint monitorHandle)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slot, out var tileSlot)) return;
            if (!IsWindow(tileSlot.Hwnd)) { RemoveSlotLocked(slot); return; }

            var bounds = GetPaneBounds(monitorHandle, GridSize, Layout);
            if (!bounds.TryGetValue(slot, out var rect)) return;
            // Restore window if minimized
            if (IsIconic(tileSlot.Hwnd))
                ShowWindow(tileSlot.Hwnd, SW_RESTORE);

            SetWindowPos(tileSlot.Hwnd, nint.Zero,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    /// <summary>Position ALL occupied slots atomically using BeginDeferWindowPos.</summary>
    public void PositionAll(nint monitorHandle)
    {
        lock (_lock) PositionAllLocked(monitorHandle);
    }

    private void PositionAllLocked(nint monitorHandle)
    {
        if (_slots.Count == 0) return;

        var bounds = GetPaneBounds(monitorHandle, GridSize, Layout);
        var deadSlots = _slots.Where(kv => !IsWindow(kv.Value.Hwnd)).Select(kv => kv.Key).ToList();
        foreach (var s in deadSlots) RemoveSlotLocked(s);

        var validSlots = _slots.Where(kv => bounds.ContainsKey(kv.Key)).ToList();
        if (validSlots.Count == 0) return;

        var hdwp = BeginDeferWindowPos(validSlots.Count);
        if (hdwp == nint.Zero) return;

        foreach (var (slot, tileSlot) in validSlots)
        {
            if (!bounds.TryGetValue(slot, out var rect)) continue;
            if (!IsWindow(tileSlot.Hwnd)) continue;

            if (IsIconic(tileSlot.Hwnd))
                ShowWindow(tileSlot.Hwnd, SW_RESTORE);

            hdwp = DeferWindowPos(hdwp, tileSlot.Hwnd, nint.Zero,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        EndDeferWindowPos(hdwp);
        _activeMonitor = monitorHandle;
    }

    // ── Grid geometry ────────────────────────────────────────────────────────

    /// <summary>
    /// Calculate pane bounds in physical pixels for a given monitor and grid size.
    /// </summary>
    public static Dictionary<int, RECT> GetPaneBounds(nint monitorHandle, int gridSize, string layout = "2x3")
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitorHandle, ref info);
        return GetPaneBounds(monitorHandle, gridSize, info.rcWork, layout);
    }

    /// <summary>
    /// Calculate pane bounds from an explicit working area rectangle.
    /// </summary>
    public static Dictionary<int, RECT> GetPaneBounds(nint monitorHandle, int gridSize, RECT wa, string layout = "2x3")
    {
        int w = wa.Right - wa.Left;
        int h = wa.Bottom - wa.Top;
        var result = new Dictionary<int, RECT>();

        switch (NormalizeGridSize(gridSize))
        {
            case 1: // fullscreen
                result[0] = new RECT(wa.Left, wa.Top, wa.Right, wa.Bottom);
                break;

            case 2: // side-by-side
                result[0] = new RECT(wa.Left, wa.Top, wa.Left + w / 2, wa.Bottom);
                result[1] = new RECT(wa.Left + w / 2, wa.Top, wa.Right, wa.Bottom);
                break;

            case 4: // 2×2
                int hw = w / 2, hh = h / 2;
                result[0] = new RECT(wa.Left, wa.Top, wa.Left + hw, wa.Top + hh);
                result[1] = new RECT(wa.Left + hw, wa.Top, wa.Right, wa.Top + hh);
                result[2] = new RECT(wa.Left, wa.Top + hh, wa.Left + hw, wa.Bottom);
                result[3] = new RECT(wa.Left + hw, wa.Top + hh, wa.Right, wa.Bottom);
                break;

            case 6 when layout == "3x2": // 3 rows × 2 cols
                int cw = w / 2;
                int rh = h / 3;
                result[0] = new RECT(wa.Left, wa.Top, wa.Left + cw, wa.Top + rh);
                result[1] = new RECT(wa.Left + cw, wa.Top, wa.Right, wa.Top + rh);
                result[2] = new RECT(wa.Left, wa.Top + rh, wa.Left + cw, wa.Top + 2 * rh);
                result[3] = new RECT(wa.Left + cw, wa.Top + rh, wa.Right, wa.Top + 2 * rh);
                result[4] = new RECT(wa.Left, wa.Top + 2 * rh, wa.Left + cw, wa.Bottom);
                result[5] = new RECT(wa.Left + cw, wa.Top + 2 * rh, wa.Right, wa.Bottom);
                break;

            case 6: // default 2×3 (2 rows × 3 cols)
                int tw = w / 3;
                int th = h / 2;
                result[0] = new RECT(wa.Left, wa.Top, wa.Left + tw, wa.Top + th);
                result[1] = new RECT(wa.Left + tw, wa.Top, wa.Left + 2 * tw, wa.Top + th);
                result[2] = new RECT(wa.Left + 2 * tw, wa.Top, wa.Right, wa.Top + th);
                result[3] = new RECT(wa.Left, wa.Top + th, wa.Left + tw, wa.Bottom);
                result[4] = new RECT(wa.Left + tw, wa.Top + th, wa.Left + 2 * tw, wa.Bottom);
                result[5] = new RECT(wa.Left + 2 * tw, wa.Top + th, wa.Right, wa.Bottom);
                break;
        }

        return result;
    }

    // ── HWND discovery ───────────────────────────────────────────────────────

    /// <summary>
    /// Find the primary visible top-level window for a process.
    /// Falls back to EnumWindows when Process.MainWindowHandle returns zero.
    /// </summary>
    public static nint FindWindowForProcess(Process process)
    {
        var hwnd = process.MainWindowHandle;
        if (hwnd != nint.Zero && IsWindow(hwnd))
            return hwnd;

        // Fallback: EnumWindows by PID
        nint found = nint.Zero;
        EnumWindows((candidateHwnd, _) =>
        {
            GetWindowThreadProcessId(candidateHwnd, out int pid);
            if (pid != process.Id) return true;
            if (!IsWindowVisible(candidateHwnd)) return true;

            // Skip owned windows
            if (GetWindow(candidateHwnd, GW_OWNER) != nint.Zero) return true;

            // Skip tool windows
            var exStyle = GetWindowLong(candidateHwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            found = candidateHwnd;
            return false; // Stop enumeration
        }, nint.Zero);

        return found;
    }

    /// <summary>Hit-test a screen point against pane bounds. Returns slot index or -1.</summary>
    public static int HitTestSlot(int screenX, int screenY, Dictionary<int, RECT> bounds)
    {
        foreach (var (slot, rect) in bounds)
        {
            if (screenX >= rect.Left && screenX < rect.Right &&
                screenY >= rect.Top && screenY < rect.Bottom)
                return slot;
        }
        return -1;
    }

    /// <summary>Get the monitor handle for the current cursor position.</summary>
    public static nint GetMonitorAtCursor()
    {
        GetCursorPos(out var pt);
        return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>Get the monitor handle for a specific screen point.</summary>
    public static nint GetMonitorAtPoint(int screenX, int screenY)
    {
        return MonitorFromPoint(new POINT { X = screenX, Y = screenY }, MONITOR_DEFAULTTONEAREST);
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private bool RemoveHwndLocked(nint hwnd)
    {
        var slot = _slots.FirstOrDefault(kv => kv.Value.Hwnd == hwnd);
        if (slot.Value is null) return false;
        return RemoveSlotLocked(slot.Key);
    }

    private bool RemoveSlotLocked(int slot)
    {
        if (!_slots.TryGetValue(slot, out var tileSlot)) return false;

        if (_processWaits.TryGetValue(tileSlot.Hwnd, out var wait))
        {
            wait.Unregister(null);
            _processWaits.Remove(tileSlot.Hwnd);
        }

        _slots.Remove(slot);
        _logger.LogInformation("KH: Removed HWND {Hwnd} from slot {Slot}", tileSlot.Hwnd, slot);
        return true;
    }

    private void RegisterProcessExitLocked(nint hwnd, int pid)
    {
        if (_processWaits.ContainsKey(hwnd)) return;

        try
        {
            var proc = Process.GetProcessById(pid);
            var handle = proc.SafeHandle;

            var registered = ThreadPool.RegisterWaitForSingleObject(
                handle.DangerousGetHandle().ToWaitHandle(),
                (state, timedOut) =>
                {
                    if (timedOut) return;
                    var h = (nint)state!;
                    _logger.LogInformation("KH: Tiled process exited, removing HWND {Hwnd}", h);
                    RemoveWindow(h);
                },
                hwnd, Timeout.Infinite, executeOnlyOnce: true);

            _processWaits[hwnd] = registered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Could not register process exit watch for PID {Pid}", pid);
        }
    }

    public static int NormalizeGridSize(int size) => size switch
    {
        0 or 1 => 1,
        2 => 2,
        6 => 6,
        _ => 4,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var wait in _processWaits.Values)
                wait.Unregister(null);
            _processWaits.Clear();
            _slots.Clear();
        }
    }

    // ── PInvoke ──────────────────────────────────────────────────────────────

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_RESTORE = 9;
    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern nint BeginDeferWindowPos(int nNumWindows);
    [DllImport("user32.dll")] public static extern nint DeferWindowPos(nint hWinPosInfo, nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool EndDeferWindowPos(nint hWinPosInfo);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, int dwFlags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hWnd, int uCmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(nint hWnd, int nIndex);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool SetWindowText(nint hWnd, string lpString);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(nint hWnd);

    /// <summary>Get the title text of a window. Returns empty string on failure.</summary>
    public static string GetWindowTitle(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

internal static class WaitHandleExtensions
{
    public static WaitHandle ToWaitHandle(this nint handle) =>
        new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: false) };
}
