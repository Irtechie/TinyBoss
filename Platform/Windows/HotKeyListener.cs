using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TinyBoss.Core;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Listens for global hotkeys via a Win32 message-only window (HWND_MESSAGE).
/// Works without any visible Avalonia window — perfect for tray-only state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HotKeyListener : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_NOREPEAT = 0x4000;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_ALT = 0x0001;
    private const int MOD_WIN = 0x0008;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;  // Generic Alt
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VOICE_RELEASE_DEBOUNCE_POLLS = 5;
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x00000010;

    // Hotkey IDs
    public const int HOTKEY_TILE = 2;
    public const int HOTKEY_REBALANCE = 3;

    private nint _hwnd;
    private Thread? _messageThread;
    private volatile bool _disposed;
    private volatile bool _reregisterRequested;
    private readonly TinyBossConfig _config;
    private readonly ILogger<HotKeyListener> _logger;
    private readonly VoiceHotkeyState _voiceState = new();
    private LowLevelKeyboardProc? _keyboardHookProc;
    private nint _keyboardHook;

    // Overlay mode: when true, poll for Escape (dismiss)
    private volatile bool _overlayActive;

    /// <summary>Fires when the voice hotkey is pressed down.</summary>
    public event Action? VoiceKeyDown;

    /// <summary>Fires when the voice hotkey is released.</summary>
    public event Action? VoiceKeyUp;

    /// <summary>Fires when the tile hotkey is pressed (toggle overlay).</summary>
    public event Action? TileKeyPressed;

    /// <summary>Fires when the rebalance hotkey is pressed.</summary>
    public event Action? RebalanceKeyPressed;

    /// <summary>Fires when Escape is pressed while overlay is active (dismiss).</summary>
    public event Action? OverlayDismiss;

    public HotKeyListener(TinyBossConfig config, ILogger<HotKeyListener> logger)
    {
        _config = config;
        _logger = logger;
    }

    public int VoiceKeyConfig => _config.VoiceKey;
    public int VoiceModConfig => _config.VoiceModifiers;

    public bool IsMovePageHeld => IsComboHeld(_config.MovePageModifiers, _config.MovePageKey);

    /// <summary>Tell the listener overlay is visible so it polls Tab/Escape.</summary>
    public void SetOverlayActive(bool active) => _overlayActive = active;

    /// <summary>Request live re-registration of hotkeys after config change.</summary>
    public void RequestReRegister() => _reregisterRequested = true;

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

        // Tile + Rebalance use RegisterHotKey; voice uses a suppressing low-level hook.
        var lastTileMods = _config.TileModifiers;
        var lastTileKey = _config.TileKey;
        var lastRebalMods = _config.RebalanceModifiers;
        var lastRebalKey = _config.RebalanceKey;

        RegisterHotKeyWithLog(HOTKEY_TILE, lastTileMods | MOD_NOREPEAT, lastTileKey, "Tile");
        RegisterHotKeyWithLog(HOTKEY_REBALANCE, lastRebalMods | MOD_NOREPEAT, lastRebalKey, "Rebalance");
        InstallVoiceKeyboardHook();

        var voiceDown = false;
        var voiceReleaseMisses = 0;
        var escWasDown = false;

        while (!_disposed)
        {
            // Live re-registration (rollback on failure)
            if (_reregisterRequested)
            {
                _reregisterRequested = false;

                var newTileMods = _config.TileModifiers;
                var newTileKey = _config.TileKey;
                var newRebalMods = _config.RebalanceModifiers;
                var newRebalKey = _config.RebalanceKey;

                UnregisterHotKey(_hwnd, HOTKEY_TILE);
                if (!RegisterHotKey(_hwnd, HOTKEY_TILE, newTileMods | MOD_NOREPEAT, newTileKey))
                {
                    _logger.LogWarning("KH: New tile hotkey failed — rolling back");
                    RegisterHotKey(_hwnd, HOTKEY_TILE, lastTileMods | MOD_NOREPEAT, lastTileKey);
                }
                else
                {
                    lastTileMods = newTileMods;
                    lastTileKey = newTileKey;
                    _logger.LogInformation("KH: Tile hotkey updated live");
                }

                UnregisterHotKey(_hwnd, HOTKEY_REBALANCE);
                if (!RegisterHotKey(_hwnd, HOTKEY_REBALANCE, newRebalMods | MOD_NOREPEAT, newRebalKey))
                {
                    _logger.LogWarning("KH: New rebalance hotkey failed — rolling back");
                    RegisterHotKey(_hwnd, HOTKEY_REBALANCE, lastRebalMods | MOD_NOREPEAT, lastRebalKey);
                }
                else
                {
                    lastRebalMods = newRebalMods;
                    lastRebalKey = newRebalKey;
                    _logger.LogInformation("KH: Rebalance hotkey updated live");
                }

                _voiceState.Reset();
            }

            if (PeekMessage(out var msg, _hwnd, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_HOTKEY)
                {
                    switch ((int)msg.wParam)
                    {
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

            if (_keyboardHook == nint.Zero)
            {
                // Fallback only. The hook path suppresses the voice key; polling cannot.
                bool voiceHeld = IsComboHeld(_config.VoiceModifiers, _config.VoiceKey);
                if (voiceHeld && !voiceDown)
                {
                    voiceDown = true;
                    voiceReleaseMisses = 0;
                    VoiceKeyDown?.Invoke();
                }
                else if (!voiceHeld && voiceDown)
                {
                    voiceReleaseMisses++;
                    if (voiceReleaseMisses >= VOICE_RELEASE_DEBOUNCE_POLLS)
                    {
                        voiceDown = false;
                        voiceReleaseMisses = 0;
                        VoiceKeyUp?.Invoke();
                    }
                }
                else if (voiceHeld)
                {
                    voiceReleaseMisses = 0;
                }
            }

            // Poll for Escape when overlay is active
            if (_overlayActive)
            {
                bool escDown = (GetAsyncKeyState(0x1B) & 0x8000) != 0; // VK_ESCAPE
                if (escDown && !escWasDown)
                    OverlayDismiss?.Invoke();
                escWasDown = escDown;
            }
            else
            {
                escWasDown = false;
            }

            Thread.Sleep(voiceDown ? 10 : 50);
        }

        UnregisterHotKey(_hwnd, HOTKEY_TILE);
        UnregisterHotKey(_hwnd, HOTKEY_REBALANCE);
        if (_keyboardHook != nint.Zero)
            UnhookWindowsHookEx(_keyboardHook);
        DestroyWindow(_hwnd);
    }

    private void InstallVoiceKeyboardHook()
    {
        _keyboardHookProc = KeyboardHookProc;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, GetModuleHandle(null), 0);
        if (_keyboardHook == nint.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("KH: Voice keyboard hook failed (error {Err}) — falling back to non-suppressing polling", err);
            HotKeyDiag("VOICE_HOOK_FAIL error={0}", err);
        }
        else
        {
            _logger.LogInformation("KH: Voice keyboard hook installed");
            HotKeyDiag("VOICE_HOOK_INSTALLED");
        }
    }

    private nint KeyboardHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode == HC_ACTION)
        {
            var message = (int)wParam;
            var isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var injected = (data.flags & LLKHF_INJECTED) != 0;
                if (!injected && IsVoiceRelevantKey((int)data.vkCode))
                {
                    var transition = _voiceState.ProcessKeyEvent(
                        (int)data.vkCode,
                        isKeyDown,
                        _config.VoiceModifiers,
                        _config.VoiceKey);

                    if (transition.Started)
                        ThreadPool.QueueUserWorkItem(_ => VoiceKeyDown?.Invoke());
                    if (transition.Stopped)
                        ThreadPool.QueueUserWorkItem(_ => VoiceKeyUp?.Invoke());
                    if (transition.Suppress)
                        return 1;
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private bool IsVoiceRelevantKey(int vkCode)
    {
        if (IsConfiguredVoiceKey(vkCode, _config.VoiceKey))
            return true;

        var modifiers = _config.VoiceModifiers;
        if ((modifiers & MOD_CONTROL) != 0 && vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL)
            return true;
        if ((modifiers & MOD_SHIFT) != 0 && vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
            return true;
        if ((modifiers & MOD_ALT) != 0 && vkCode is VK_MENU or VK_LMENU or VK_RMENU)
            return true;
        if ((modifiers & MOD_WIN) != 0 && vkCode is VK_LWIN or VK_RWIN)
            return true;

        return false;
    }

    private static bool IsConfiguredVoiceKey(int vkCode, int key)
    {
        if (key == VK_SHIFT)
            return vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT;
        if (key == VK_CONTROL)
            return vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL;
        if (key == VK_MENU)
            return vkCode is VK_MENU or VK_LMENU or VK_RMENU;

        return vkCode == key;
    }

    private bool IsComboHeld(int modifiers, int key)
    {
        if ((GetAsyncKeyState(key) & 0x8000) == 0) return false;
        if ((modifiers & MOD_CONTROL) != 0 && (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0) return false;
        if ((modifiers & MOD_SHIFT) != 0 && (GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0) return false;
        if ((modifiers & MOD_ALT) != 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) == 0) return false;
        if ((modifiers & MOD_WIN) != 0 &&
            (GetAsyncKeyState(VK_LWIN) & 0x8000) == 0 &&
            (GetAsyncKeyState(VK_RWIN) & 0x8000) == 0) return false;
        return true;
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

    private static void HotKeyDiag(string fmt, params object[] args)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {string.Format(fmt, args)}";
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "TinyBoss", "voice_diag.log");
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
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

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }
}
