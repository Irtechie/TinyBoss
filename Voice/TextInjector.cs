using System.Runtime.InteropServices;
using KittenHerder.Core;

namespace KittenHerder.Voice;

/// <summary>
/// Injects transcribed voice text into managed session stdin.
/// Uses the existing InjectHandler path (Process.StandardInput).
/// Voice targets KH-managed sessions only (v1 scope decision).
/// </summary>
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
    /// Inject text into the specified session, or the focused managed session if sessionId is null.
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
                return (false, "No managed session focused");
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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
