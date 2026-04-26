using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TinyBoss.Core;

namespace TinyBoss.Voice;

/// <summary>
/// Injects transcribed voice text into the target window.
/// Priority: managed session stdin → SendInput keyboard simulation (any focused window).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TextInjector
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<TextInjector> _logger;

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
                // Fallback: type into whatever window is focused via keyboard simulation
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
            TypeViaKeyboard(text, moveToEnd: true);
            _logger.LogInformation("KH: Voice appended {N} chars via SendInput into focused window", text.Length);
            return (true, "Typed into focused window");
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
            TypeViaKeyboard(text, moveToEnd: true);
            return (true, "Typed into focused window");
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
        }
    }

    private static void SendVirtualKey(ushort key)
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = INPUT_KEYBOARD, wVk = key };
        inputs[1] = new INPUT { type = INPUT_KEYBOARD, wVk = key, dwFlags = KEYEVENTF_KEYUP };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
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
}
