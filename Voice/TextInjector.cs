using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using TinyBoss.Core;
using TinyBoss.Platform.Windows;

namespace TinyBoss.Voice;

/// <summary>
/// Injects transcribed voice text into the target window.
/// Priority: managed session stdin -> clipboard paste for focused text -> SendInput keyboard simulation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TextInjector
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<TextInjector> _logger;
    private const int ClipboardRestoreDelayMs = 1500;
    private const int ClipboardOpenAttempts = 24;
    private const int ClipboardOpenRetryDelayMs = 25;
    private const int SendInputBatchChars = 128;
    private const int SendInputFallbackMaxChars = 24;
    private const int ErrorInvalidWindowHandle = 1400;
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
            var append = await AppendViaFocusedWindowAsync(text, ct);
            _logger.LogInformation("KH: Voice append into focused window success={Success} chars={N} method={Method}",
                append.Success, text.Length, append.Message);
            return append.Success
                ? (true, $"Typed into focused window ({append.Message})")
                : (false, append.Message);
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
            var append = await AppendViaFocusedWindowAsync(text, ct);
            return append.Success
                ? (true, $"Typed into focused window ({append.Message})")
                : (false, append.Message);
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

        var offset = 0;
        while (offset < text.Length)
        {
            var charCount = Math.Min(SendInputBatchChars, text.Length - offset);
            var inputs = new INPUT[charCount * 2];

            for (var i = 0; i < charCount; i++)
            {
                var c = text[offset + i];
                var inputIndex = i * 2;
                inputs[inputIndex] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    wScan = c,
                    dwFlags = KEYEVENTF_UNICODE
                };
                inputs[inputIndex + 1] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    wScan = c,
                    dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                };
            }

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            offset += charCount;

            if (offset < text.Length)
                Thread.Sleep(1);
        }
    }

    private async Task<FocusedAppendAttempt> AppendViaFocusedWindowAsync(string text, CancellationToken ct)
    {
        var target = CaptureFocusedWindowTarget();
        if (target.IsTerminal)
        {
            if (TryWriteConsoleInputBuffer(text, target, out var consoleMessage))
                return new FocusedAppendAttempt(true, consoleMessage);

            _logger.LogInformation(
                "KH: Voice console input buffer failed for {N} chars target={Target}: {Reason}",
                text.Length, target.Description, consoleMessage);

            TypeViaKeyboard(text, moveToEnd: true);
            await Task.CompletedTask;
            return new FocusedAppendAttempt(
                true,
                $"terminal SendInput fallback after console input failed ({consoleMessage}); target={target.Description}");
        }

        var paste = TryPasteViaClipboard(text, moveToEnd: true);
        if (!paste.Success)
        {
            _logger.LogInformation(
                "KH: Voice clipboard paste failed for {N} chars target={Target}: {Reason}",
                text.Length, target.Description, paste.Method);

            if (text.Length > SendInputFallbackMaxChars)
            {
                return new FocusedAppendAttempt(
                    false,
                    $"Clipboard paste failed ({paste.Method}); transcript preserved instead of slow SendInput; target={target.Description}");
            }

            TypeViaKeyboard(text, moveToEnd: true);
            return new FocusedAppendAttempt(true, $"batched SendInput after clipboard failure ({paste.Method}); target={target.Description}");
        }

        await Task.Delay(250, ct);
        return new FocusedAppendAttempt(true, $"{paste.Method}; target={target.Description}");
    }

    private static bool TryWriteConsoleInputBuffer(
        string text,
        FocusedWindowTarget target,
        out string message)
    {
        if (!target.ClassName.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase))
        {
            message = $"console input unsupported for class={target.ClassName}";
            return false;
        }

        if (target.ProcessId == 0)
        {
            message = "console input missing target process id";
            return false;
        }

        var attached = AttachConsole(target.ProcessId);
        if (!attached && Marshal.GetLastWin32Error() == ErrorAccessDenied)
        {
            FreeConsole();
            attached = AttachConsole(target.ProcessId);
        }

        if (!attached)
        {
            message = $"AttachConsole failed lastError={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            using var input = CreateFile(
                "CONIN$",
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                nint.Zero,
                OpenExisting,
                0,
                nint.Zero);

            if (input.IsInvalid)
            {
                message = $"CreateFile(CONIN$) failed lastError={Marshal.GetLastWin32Error()}";
                return false;
            }

            var records = BuildConsoleInputRecords(text);
            if (records.Length == 0)
            {
                message = "console input had no records";
                return false;
            }

            const int maxRecordsPerWrite = 2048;
            var writtenTotal = 0;
            for (var offset = 0; offset < records.Length; offset += maxRecordsPerWrite)
            {
                var count = Math.Min(maxRecordsPerWrite, records.Length - offset);
                var chunk = new CONSOLE_INPUT_RECORD[count];
                Array.Copy(records, offset, chunk, 0, count);

                if (!WriteConsoleInput(input, chunk, (uint)chunk.Length, out var written) ||
                    written != chunk.Length)
                {
                    message = $"WriteConsoleInput failed at {writtenTotal}/{records.Length} records wrote={written}/{chunk.Length} lastError={Marshal.GetLastWin32Error()}";
                    return false;
                }

                writtenTotal += (int)written;
            }

            message = $"console input buffer chars={text.Length} records={writtenTotal}; target={target.Description}";
            return true;
        }
        finally
        {
            FreeConsole();
        }
    }

    private static CONSOLE_INPUT_RECORD[] BuildConsoleInputRecords(string text)
    {
        var records = new CONSOLE_INPUT_RECORD[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var inputIndex = i * 2;
            records[inputIndex] = CreateConsoleKeyRecord(keyDown: true, c);
            records[inputIndex + 1] = CreateConsoleKeyRecord(keyDown: false, c);
        }

        return records;
    }

    private static CONSOLE_INPUT_RECORD CreateConsoleKeyRecord(bool keyDown, char c) => new()
    {
        EventType = KEY_EVENT,
        KeyEvent = new CONSOLE_KEY_EVENT_RECORD
        {
            KeyDown = keyDown ? 1 : 0,
            RepeatCount = 1,
            VirtualKeyCode = VK_PACKET,
            VirtualScanCode = 0,
            UnicodeChar = (ushort)c,
            ControlKeyState = 0
        }
    };

    private static ClipboardPasteAttempt TryPasteViaClipboard(string text, bool moveToEnd)
    {
        string? previousText = null;
        var hadText = TryReadClipboardText(out previousText);
        var formatCount = CountClipboardFormats();

        if (!TrySetClipboardText(text, out var setFailure))
        {
            if (hadText)
                TrySetClipboardText(previousText ?? string.Empty, out _);
            return new ClipboardPasteAttempt(false, setFailure);
        }

        if (moveToEnd)
            SendVirtualKey(VK_END);
        var pasteInput = SendPasteChord();
        if (!pasteInput.Success)
        {
            if (hadText)
                TrySetClipboardText(previousText ?? string.Empty, out _);
            return new ClipboardPasteAttempt(false, pasteInput.Message);
        }

        if (hadText)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(ClipboardRestoreDelayMs);
                TrySetClipboardText(previousText ?? string.Empty, out _);
            });

            return new ClipboardPasteAttempt(true, "clipboard/ctrl+v (restored previous text)");
        }

        var method = formatCount > 0
            ? "clipboard/ctrl+v (replaced non-text clipboard)"
            : "clipboard/ctrl+v";
        return new ClipboardPasteAttempt(true, method);
    }

    private static void SendVirtualKey(ushort key)
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = INPUT_KEYBOARD, wVk = key };
        inputs[1] = new INPUT { type = INPUT_KEYBOARD, wVk = key, dwFlags = KEYEVENTF_KEYUP };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static PasteInputAttempt SendPasteChord()
    {
        INPUT[] inputs =
        [
            new INPUT { type = INPUT_KEYBOARD, wVk = VK_CONTROL },
            new INPUT { type = INPUT_KEYBOARD, wVk = VK_V },
            new INPUT { type = INPUT_KEYBOARD, wVk = VK_V, dwFlags = KEYEVENTF_KEYUP },
            new INPUT { type = INPUT_KEYBOARD, wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP },
        ];
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
            return new PasteInputAttempt(true, "clipboard/ctrl+v");

        return new PasteInputAttempt(
            false,
            $"SendInput paste chord ctrl+v sent {sent}/{inputs.Length} lastError={Marshal.GetLastWin32Error()}");
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

    private static bool TrySetClipboardText(string text, out string failure)
    {
        failure = string.Empty;
        if (!TryOpenClipboard())
        {
            failure = $"OpenClipboard failed lastError={Marshal.GetLastWin32Error()}";
            return false;
        }

        nint handle = nint.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
            if (handle == nint.Zero)
            {
                failure = $"GlobalAlloc failed lastError={Marshal.GetLastWin32Error()}";
                return false;
            }

            var ptr = GlobalLock(handle);
            if (ptr == nint.Zero)
            {
                failure = $"GlobalLock failed lastError={Marshal.GetLastWin32Error()}";
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (!EmptyClipboard())
            {
                failure = $"EmptyClipboard failed lastError={Marshal.GetLastWin32Error()}";
                return false;
            }

            if (SetClipboardData(CF_UNICODETEXT, handle) == nint.Zero)
            {
                failure = $"SetClipboardData failed lastError={Marshal.GetLastWin32Error()}";
                return false;
            }

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
        for (var i = 0; i < ClipboardOpenAttempts; i++)
        {
            var owner = GetClipboardOwnerWindow();
            if (owner == nint.Zero)
                return false;

            if (OpenClipboard(owner))
                return true;

            if (Marshal.GetLastWin32Error() == ErrorInvalidWindowHandle)
                ResetClipboardOwnerWindow(owner);

            Thread.Sleep(ClipboardOpenRetryDelayMs);
        }

        return false;
    }

    private static nint GetClipboardOwnerWindow()
    {
        if (_clipboardOwnerWindow != nint.Zero && IsWindow(_clipboardOwnerWindow))
            return _clipboardOwnerWindow;

        lock (ClipboardOwnerLock)
        {
            if (_clipboardOwnerWindow != nint.Zero && IsWindow(_clipboardOwnerWindow))
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

    private static void ResetClipboardOwnerWindow(nint owner)
    {
        lock (ClipboardOwnerLock)
        {
            if (_clipboardOwnerWindow == owner)
                _clipboardOwnerWindow = nint.Zero;
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

    private static FocusedWindowTarget CaptureFocusedWindowTarget()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero)
            return FocusedWindowTarget.Unknown;

        GetWindowThreadProcessId(hwnd, out var pid);

        var className = "";
        var classBuilder = new StringBuilder(256);
        if (GetClassName(hwnd, classBuilder, classBuilder.Capacity) > 0)
            className = classBuilder.ToString();

        var processName = "";
        if (pid != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch
            {
                processName = $"pid:{pid}";
            }
        }

        var isTerminal = false;
        try
        {
            isTerminal = TerminalDetector.IsTerminalWindow(hwnd);
        }
        catch
        {
            // Target classification is diagnostic/policy only; injection can still proceed.
        }

        return new FocusedWindowTarget(hwnd, pid, processName, className, isTerminal);
    }

    // ── SendInput P/Invoke (win-x64) ─────────────────────────────────────────

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort KEY_EVENT = 0x0001;
    private const ushort VK_END = 0x23;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_PACKET = 0xE7;
    private const ushort VK_V = 0x56;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int ErrorAccessDenied = 5;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_INPUT_RECORD
    {
        public ushort EventType;
        public CONSOLE_KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_KEY_EVENT_RECORD
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public ushort UnicodeChar;
        public uint ControlKeyState;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleInputW")]
    private static extern bool WriteConsoleInput(
        SafeFileHandle hConsoleInput,
        CONSOLE_INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

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

    private sealed record ClipboardPasteAttempt(bool Success, string Method);

    private sealed record PasteInputAttempt(bool Success, string Message);

    private sealed record FocusedAppendAttempt(bool Success, string Message);

    private sealed record FocusedWindowTarget(
        nint Hwnd,
        uint ProcessId,
        string ProcessName,
        string ClassName,
        bool IsTerminal)
    {
        public static FocusedWindowTarget Unknown { get; } = new(
            nint.Zero,
            0,
            "unknown",
            "unknown",
            false);

        public string Description =>
            $"terminal={IsTerminal},class={ClassName},process={ProcessName},hwnd=0x{Hwnd:X}";
    }

}
