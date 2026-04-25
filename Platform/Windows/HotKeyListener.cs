using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KittenHerder.Core;

namespace KittenHerder.Platform.Windows;

/// <summary>
/// Listens for global hotkeys via a Win32 message-only window (HWND_MESSAGE).
/// Works without any visible Avalonia window — perfect for tray-only state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HotKeyListener : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_NOREPEAT = 0x4000;
    private const int VK_SPACE = 0x20;

    // Hotkey IDs
    public const int HOTKEY_VOICE = 1;
    public const int HOTKEY_TILE = 2;
    public const int HOTKEY_REBALANCE = 3;

    private nint _hwnd;
    private Thread? _messageThread;
    private volatile bool _disposed;
    private readonly TinyBossConfig _config;
    private readonly ILogger<HotKeyListener> _logger;

    // Overlay mode: when true, poll for Tab (cycle layout) and Escape (dismiss)
    private volatile bool _overlayActive;

    /// <summary>Fires when the voice hotkey is pressed down.</summary>
    public event Action? VoiceKeyDown;

    /// <summary>Fires when the voice hotkey is released.</summary>
    public event Action? VoiceKeyUp;

    /// <summary>Fires when the tile hotkey is pressed (toggle overlay).</summary>
    public event Action? TileKeyPressed;

    /// <summary>Fires when the rebalance hotkey is pressed.</summary>
    public event Action? RebalanceKeyPressed;

    /// <summary>Fires when Tab is pressed while overlay is active (cycle layout).</summary>
    public event Action? OverlayCycleLayout;

    /// <summary>Fires when Escape is pressed while overlay is active (dismiss).</summary>
    public event Action? OverlayDismiss;

    public HotKeyListener(TinyBossConfig config, ILogger<HotKeyListener> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Tell the listener overlay is visible so it polls Tab/Escape.</summary>
    public void SetOverlayActive(bool active) => _overlayActive = active;

    public void Start()
    {
        _messageThread = new Thread(MessagePumpThread)
        {
            Name = "HotKeyListener",
            IsBackground = true,
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    private void MessagePumpThread()
    {
        _hwnd = CreateMessageOnlyWindow();
        if (_hwnd == 0)
        {
            _logger.LogError("KH: Failed to create message-only window for hotkeys");
            return;
        }

        RegisterHotKeyWithLog(HOTKEY_VOICE, _config.VoiceModifiers | MOD_NOREPEAT, _config.VoiceKey, "Voice (Ctrl+Shift+Space)");
        RegisterHotKeyWithLog(HOTKEY_TILE, _config.TileModifiers | MOD_NOREPEAT, _config.TileKey, "Tile (Ctrl+Shift+G)");
        RegisterHotKeyWithLog(HOTKEY_REBALANCE, _config.RebalanceModifiers | MOD_NOREPEAT, _config.RebalanceKey, "Rebalance (Ctrl+Shift+R)");

        var voiceDown = false;
        var tabWasDown = false;
        var escWasDown = false;

        while (!_disposed)
        {
            if (PeekMessage(out var msg, _hwnd, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_HOTKEY)
                {
                    switch ((int)msg.wParam)
                    {
                        case HOTKEY_VOICE:
                            voiceDown = true;
                            VoiceKeyDown?.Invoke();
                            break;
                        case HOTKEY_TILE:
                            TileKeyPressed?.Invoke();
                            break;
                        case HOTKEY_REBALANCE:
                            RebalanceKeyPressed?.Invoke();
                            break;
                    }
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            // Poll for voice key release
            if (voiceDown)
            {
                bool spaceHeld = (GetAsyncKeyState(VK_SPACE) & 0x8000) != 0;
                if (!spaceHeld)
                {
                    voiceDown = false;
                    VoiceKeyUp?.Invoke();
                }
            }

            // Poll for Tab/Escape when overlay is active
            if (_overlayActive)
            {
                bool tabDown = (GetAsyncKeyState(0x09) & 0x8000) != 0; // VK_TAB
                if (tabDown && !tabWasDown)
                    OverlayCycleLayout?.Invoke();
                tabWasDown = tabDown;

                bool escDown = (GetAsyncKeyState(0x1B) & 0x8000) != 0; // VK_ESCAPE
                if (escDown && !escWasDown)
                    OverlayDismiss?.Invoke();
                escWasDown = escDown;
            }
            else
            {
                tabWasDown = false;
                escWasDown = false;
            }

            Thread.Sleep(voiceDown ? 10 : 50);
        }

        UnregisterHotKey(_hwnd, HOTKEY_VOICE);
        UnregisterHotKey(_hwnd, HOTKEY_TILE);
        UnregisterHotKey(_hwnd, HOTKEY_REBALANCE);
        DestroyWindow(_hwnd);
    }

    private void RegisterHotKeyWithLog(int id, int modifiers, int vk, string label)
    {
        if (!RegisterHotKey(_hwnd, id, modifiers, vk))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("KH: RegisterHotKey failed for {Label} (error {Err}) — hotkey may be in use", label, err);
        }
        else
        {
            _logger.LogInformation("KH: Hotkey registered: {Label}", label);
        }
    }

    private nint CreateMessageOnlyWindow()
    {
        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = DefWindowProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "TinyBossHotKey",
        };

        if (RegisterClassEx(ref wndClass) == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = already registered
            return 0;

        return CreateWindowEx(0, "TinyBossHotKey", null, 0, 0, 0, 0, 0,
            HWND_MESSAGE, 0, GetModuleHandle(null), 0);
    }

    public void Dispose()
    {
        _disposed = true;
        _messageThread?.Join(2000);
    }

    // ── PInvoke ──────────────────────────────────────────────────────────────

    private static readonly nint HWND_MESSAGE = new(-3);
    private const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);
}
