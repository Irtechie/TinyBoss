using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static TinyBoss.Platform.Windows.TilingCoordinator;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Watches for window drag events via SetWinEventHook.
/// Detects when any window starts/stops being moved/resized,
/// and tracks cursor position during drags for zone highlighting.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DragWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private nint _hookMoveSize;
    private nint _hookLocationChange;
    private WinEventDelegate? _moveSizeDelegate;
    private WinEventDelegate? _locationDelegate;
    private readonly ILogger<DragWatcher> _logger;

    private volatile bool _dragging;
    private nint _draggedHwnd;
    private DateTime _lastLocationEvent = DateTime.MinValue;
    private bool _disposed;

    /// <summary>Fires when a window starts being dragged. Payload: HWND.</summary>
    public event Action<nint>? DragStarted;

    /// <summary>Fires during drag with cursor position (throttled ~16ms). Payload: (screenX, screenY).</summary>
    public event Action<int, int>? DragMoved;

    /// <summary>Fires when drag ends. Payload: (HWND, screenX, screenY).</summary>
    public event Action<nint, int, int>? DragEnded;

    public bool IsDragging => _dragging;

    public DragWatcher(ILogger<DragWatcher> logger)
    {
        _logger = logger;
    }

    /// <summary>Install WinEvent hooks. Must be called from a thread with a message pump.</summary>
    public void Install()
    {
        _moveSizeDelegate = OnMoveSizeEvent;
        _locationDelegate = OnLocationChangeEvent;

        _hookMoveSize = SetWinEventHook(
            EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND,
            nint.Zero, _moveSizeDelegate, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _hookLocationChange = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            nint.Zero, _locationDelegate, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hookMoveSize == nint.Zero || _hookLocationChange == nint.Zero)
            _logger.LogError("KH: SetWinEventHook failed for drag detection");
        else
            _logger.LogInformation("KH: Drag watcher installed");
    }

    private void OnMoveSizeEvent(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Only care about top-level windows (idObject == OBJID_WINDOW, idChild == CHILDID_SELF)
        if (idObject != 0 || idChild != 0) return;

        if (eventType == EVENT_SYSTEM_MOVESIZESTART)
        {
            _dragging = true;
            _draggedHwnd = hwnd;
            DragStarted?.Invoke(hwnd);
        }
        else if (eventType == EVENT_SYSTEM_MOVESIZEEND)
        {
            _dragging = false;
            GetCursorPos(out var pt);
            DragEnded?.Invoke(_draggedHwnd, pt.X, pt.Y);
            _draggedHwnd = nint.Zero;
        }
    }

    private void OnLocationChangeEvent(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_dragging || hwnd != _draggedHwnd) return;
        if (idObject != 0 || idChild != 0) return;

        // Throttle to ~16ms (60fps)
        var now = DateTime.UtcNow;
        if ((now - _lastLocationEvent).TotalMilliseconds < 16) return;
        _lastLocationEvent = now;

        GetCursorPos(out var pt);
        DragMoved?.Invoke(pt.X, pt.Y);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookMoveSize != nint.Zero) UnhookWinEvent(_hookMoveSize);
        if (_hookLocationChange != nint.Zero) UnhookWinEvent(_hookLocationChange);
    }

    // ── PInvoke ──────────────────────────────────────────────────────────────

    private delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);
}
