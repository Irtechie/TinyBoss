using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using TinyBoss.Core;

namespace TinyBoss.Voice;

/// <summary>
/// Injects transcribed voice text into the target window.
/// Priority: managed session stdin -> clipboard paste for long focused text -> SendInput keyboard simulation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TextInjector
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<TextInjector> _logger;
    private const int ClipboardPasteThresholdChars = 80;
    private static readonly object ClipboardOwnerLock = new();
    private static nint _clipboardOwnerWindow;

    public TextInjector(SessionRegistry registry, ILogger<TextInjector> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Inject text into the specified session, or the focused window if sessionId is null.
    /// Managed sessions use stdin; regular windows get keyboard simulation via SendInput.
    /// Returns (success, message) describing the result.
    /// </summary>
    public async Task<(bool Success, string Message)> InjectAsync(string text, string? targetSessionId, CancellationToken ct = default)
    {
        ManagedSession? session;

        if (targetSessionId is not null)
        {
            session = _registry.Get(targetSessionId);
            if (session is null)
                return (false, "Target session no longer exists");
        }
        else
        {
            session = FindFocusedManagedSession();
            if (session is null)
            {
                // Fallback: type into whatever window is focused via keyboard simulation.
                TypeViaKeyboard(text);
                _logger.LogInformation("KH: Voice typed {N} chars via SendInput into focused window", text.Length);
                return (true, "Typed into focused window");
            }
        }

        try
        {
            if (session.Process.HasExited)
                return (false, "Target session has exited");

            // Append newline so the command executes
            var textWithNewline = text.EndsWith('\n') ? text : text + "\n";
            await session.Process.StandardInput.WriteAsync(textWithNewline.AsMemory(), ct);

            _logger.LogInformation("KH: Voice injected {N} chars into session {Id} ({Cmd})",
                text.Length, session.SessionId, session.Command);

            return (true, $"Injected into {session.Command}");
        }
        catch (InvalidOperationException)
        {
            return (false, "Session stdin not available (visible window sessions don't redirect stdin)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Voice inject failed for session {Id}", session.SessionId);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Append text without sending Enter. Used for dictation into focused windows.
    /// </summary>
    public async Task<(bool Success, string Message)> AppendAsync(string text, string? targetSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (true, "Nothing to append");

        ManagedSession? session = null;
        if (targetSessionId is not null)
        {
            session = _registry.Get(targetSessionId);
            if (session is null)
                return (false, "Target session no longer exists");
        }

        if (session is null)
        {
            var method = await AppendViaFocusedWindowAsync(text, ct);
            _logger.LogInformation("KH: Voice appended {N} chars via {Method} into focused window", text.Length, method);
            return (true, $"Typed into focused window ({method})");
        }

        try
        {
            if (session.Process.HasExited)
                return (false, "Target session has exited");

            await session.Process.StandardInput.WriteAsync(text.AsMemory(), ct);
            _logger.LogInformation("KH: Voice appended {N} chars into session {Id} ({Cmd})",
                text.Length, session.SessionId, session.Command);
            return (true, $"Injected into {session.Command}");
        }
        catch (InvalidOperationException)
        {
            var method = await AppendViaFocusedWindowAsync(text, ct);
            return (true, $"Typed into focused window ({method})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Voice append failed for session {Id}", session.SessionId);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Type text into the focused window using SendInput with Unicode characters.
    /// Does NOT send Enter — user reviews transcription and confirms.
    /// </summary>
    public void TypeViaKeyboard(string text, bool moveToEnd = false)
    {
        if (moveToEnd)
            SendVirtualKey(VK_END);

        var inputs = new INPUT[2];
        var sentChars = 0;
        foreach (char c in text)
        {
            // Key down
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE
            };
            // Key up
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
            };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            sentChars++;
            if (sentChars % 64 == 0)
                Thread.Sleep(2);
        }
    }

    private async Task<string> AppendViaFocusedWindowAsync(string text, CancellationToken ct)
    {
        if (text.Length < ClipboardPasteThresholdChars)
        {
            TypeViaKeyboard(text, moveToEnd: true);
            return "SendInput";
        }

        if (!TryPasteViaClipboard(text, moveToEnd: true))
        {
            TypeViaKeyboard(text, moveToEnd: true);
            return "SendInput";
        }

        await Task.Delay(250, ct);
        return "clipboard";
    }

    private static bool TryPasteViaClipboard(string text, bool moveToEnd)
    {
        string? previousText = null;
        var hadText = TryReadClipboardText(out previousText);
        if (!hadText && CountClipboardFormats() > 0)
            return false;

        if (!TrySetClipboardText(text))
        {
            if (hadText)
                TrySetClipboardText(previousText ?? string.Empty);
            return false;
        }

        if (moveToEnd)
            SendVirtualKey(VK_END);
        SendPasteChord();

        if (hadText)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                TrySetClipboardText(previousText ?? string.Empty);
            });
        }

        return true;
    }

    private static void SendVirtualKey(ushort key)
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = INPUT_KEYBOARD, wVk = key };
        inputs[1] = new INPUT { type = INPUT_KEYBOARD, wVk = key, dwFlags = KEYEVENTF_KEYUP };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendPasteChord()
    {
        var inputs = new INPUT[4];
        inputs[0] = new INPUT { type = INPUT_KEYBOARD, wVk = VK_CONTROL };
        inputs[1] = new INPUT { type = INPUT_KEYBOARD, wVk = VK_V };
        inputs[2] = new INPUT { type = INPUT_KEYBOARD, wVk = VK_V, dwFlags = KEYEVENTF_KEYUP };
        inputs[3] = new INPUT { type = INPUT_KEYBOARD, wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP };
        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool TryReadClipboardText(out string? text)
    {
        text = null;
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
            return false;

        if (!TryOpenClipboard())
            return false;

        nint handle = nint.Zero;
        nint ptr = nint.Zero;
        try
        {
            handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == nint.Zero)
                return false;

            ptr = GlobalLock(handle);
            if (ptr == nint.Zero)
                return false;

            text = Marshal.PtrToStringUni(ptr);
            return text is not null;
        }
        finally
        {
            if (ptr != nint.Zero)
                GlobalUnlock(handle);
            CloseClipboard();
        }
    }

    private static bool TrySetClipboardText(string text)
    {
        if (!TryOpenClipboard())
            return false;

        nint handle = nint.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
            if (handle == nint.Zero)
                return false;

            var ptr = GlobalLock(handle);
            if (ptr == nint.Zero)
                return false;

            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (!EmptyClipboard())
                return false;

            if (SetClipboardData(CF_UNICODETEXT, handle) == nint.Zero)
                return false;

            handle = nint.Zero; // Clipboard owns it now.
            return true;
        }
        finally
        {
            if (handle != nint.Zero)
                GlobalFree(handle);
            CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        var owner = GetClipboardOwnerWindow();
        if (owner == nint.Zero)
            return false;

        for (var i = 0; i < 8; i++)
        {
            if (OpenClipboard(owner))
                return true;
            Thread.Sleep(25);
        }

        return false;
    }

    private static nint GetClipboardOwnerWindow()
    {
        if (_clipboardOwnerWindow != nint.Zero)
            return _clipboardOwnerWindow;

        lock (ClipboardOwnerLock)
        {
            if (_clipboardOwnerWindow != nint.Zero)
                return _clipboardOwnerWindow;

            _clipboardOwnerWindow = CreateWindowEx(
                0,
                "STATIC",
                "TinyBossClipboardOwner",
                0,
                0,
                0,
                0,
                0,
                HWND_MESSAGE,
                nint.Zero,
                GetModuleHandle(null),
                nint.Zero);

            return _clipboardOwnerWindow;
        }
    }

    /// <summary>
    /// Find which managed session (if any) owns the currently focused window.
    /// Matches by PID: GetForegroundWindow → GetWindowThreadProcessId → SessionRegistry lookup.
    /// </summary>
    private ManagedSession? FindFocusedManagedSession()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0) return null;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            foreach (var session in _registry.GetSnapshot())
            {
                try
                {
                    if (!session.Process.HasExited && session.Process.Id == pid)
                        return session;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KH: Failed to find focused managed session");
        }

        return null;
    }

    // ── SendInput P/Invoke (win-x64) ─────────────────────────────────────────

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_END = 0x23;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private static readonly nint HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public nint dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern int CountClipboardFormats();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint hMem);
}
