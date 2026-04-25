using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using TinyBoss.Core;
using TinyBoss.Protocol;

namespace TinyBoss.Handlers;

/// <summary>
/// Handles "spawn" messages: validates allowlist, starts process, pumps stdout.
/// </summary>
public sealed class SpawnHandler
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<SpawnHandler> _logger;
    private readonly HashSet<string> _allowlist;

    // Env var override lets logos.env or service config point at a custom path.
    // Default: allowed_executables.txt alongside the exe (works under any service account).
    private static readonly string AllowlistPath =
        Environment.GetEnvironmentVariable("TinyBoss_ALLOWLIST_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "allowed_executables.txt");

    public SpawnHandler(SessionRegistry registry, ILogger<SpawnHandler> logger)
    {
        _registry = registry;
        _logger = logger;
        _allowlist = LoadAllowlist();
    }

    public async Task HandleAsync(
        KhEnvelope envelope,
        WebSocket ws,
        Func<KhEnvelope, Task> sendAsync,
        CancellationToken ct)
    {
        var payload = envelope.Payload.Deserialize<SpawnPayload>();
        if (payload is null)
        {
            await sendAsync(ErrorEnvelope(envelope.SessionId, "Invalid spawn payload"));
            return;
        }

        // Accept either "executable" (new) or "command" (legacy) field
        var commandExe = ResolveExecutable(payload.Executable ?? payload.Command ?? string.Empty);
        // Match by full path OR bare filename so allowlists like "inferno.exe" work
        // even when callers send the full path "E:\...\inferno.exe"
        if (!_allowlist.Contains(commandExe) && !_allowlist.Contains(Path.GetFileName(commandExe)))
        {
            _logger.LogWarning("KH: Spawn blocked — {Cmd} not in allowlist", commandExe);
            await sendAsync(ErrorEnvelope(envelope.SessionId, $"Executable not in allowlist: {commandExe}"));
            return;
        }

        var sessionId = envelope.SessionId ?? Guid.NewGuid().ToString("N");

        Process proc;
        if (payload.Visible)
        {
            // Open a visible console window — user sees and interacts with it directly.
            // Launch via cmd.exe so we can set the window title before the CLI starts.
            var title = payload.Title ?? commandExe;
            var cmdArgs = $"/k \"title {title} && {commandExe}\"";
            // Append any extra args to the CLI invocation inside the cmd wrapper
            if (payload.Args is { Length: > 0 })
                cmdArgs = $"/k \"title {title} && {commandExe} {string.Join(" ", payload.Args.Select(a => $"\"{a}\""))}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = payload.Cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            try
            {
                proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KH: Failed to open visible window for {Cmd}", commandExe);
                await sendAsync(ErrorEnvelope(sessionId, ex.Message));
                return;
            }
        }
        else
        {
            // Headless — stdin/stdout/stderr redirected for programmatic control.
            var psi = new ProcessStartInfo
            {
                FileName = commandExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = payload.Cwd ?? AppContext.BaseDirectory,
            };
            if (payload.Args is { Length: > 0 })
                foreach (var arg in payload.Args)
                    psi.ArgumentList.Add(arg);

            try
            {
                proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                proc.StandardInput.AutoFlush = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KH: Failed to start headless process {Cmd}", commandExe);
                await sendAsync(ErrorEnvelope(sessionId, ex.Message));
                return;
            }
        }

        var session = new ManagedSession(sessionId, payload.Executable ?? payload.Command ?? commandExe, payload.Cwd, payload.SourceSurface, proc);
        _registry.TryAdd(session);

        _logger.LogInformation("KH: Spawned {Cmd} → session {Id} (pid {Pid}) visible={Visible}", commandExe, sessionId, proc.Id, payload.Visible);
        await sendAsync(SpawnAckEnvelope(sessionId, proc, commandExe, payload.Cwd, payload.SourceSurface));

        // Only pump stdout/stderr for headless sessions (visible sessions own their own console)
        if (!payload.Visible)
        {
            _ = Task.Run(() => StreamAsync(session, "stdout", session.StdoutReader, () => session.PumpStdoutAsync(ct), sendAsync, ct), ct);
            _ = Task.Run(() => StreamAsync(session, "stderr", session.StderrReader, () => session.PumpStderrAsync(ct), sendAsync, ct), ct);
        }

        // Watch for process exit in background
        _ = Task.Run(() => WatchExitAsync(session, sendAsync, ct), ct);
    }

    private async Task StreamAsync(
        ManagedSession session,
        string fd,
        ChannelReader<string> reader,
        Func<Task> pumpStarter,
        Func<KhEnvelope, Task> sendAsync,
        CancellationToken ct)
    {
        var pumpTask = pumpStarter();
        var batch = new List<string>(64);
        var batchTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));

        async Task FlushBatch()
        {
            if (batch.Count == 0) return;
            var frame = BuildStreamOut(session.SessionId, fd, [.. batch]);
            batch.Clear();
            try { await sendAsync(frame); } catch { /* WS closed */ }
        }

        // Drain reader with 64-line / 16ms batch window
        while (!ct.IsCancellationRequested)
        {
            // Try to fill batch
            while (batch.Count < 64 && reader.TryRead(out var line))
                batch.Add(line);

            if (batch.Count >= 64)
            {
                await FlushBatch();
                continue;
            }

            // Wait for timer tick or more data
            var tickTask = batchTimer.WaitForNextTickAsync(ct).AsTask();
            var readTask = reader.WaitToReadAsync(ct).AsTask();
            var completed = await Task.WhenAny(tickTask, readTask);

            if (completed == tickTask)
                await FlushBatch();

            if (completed == readTask && !await readTask.ContinueWith(t => t.IsCompletedSuccessfully && t.Result))
                break;   // channel completed (EOF)
        }

        await FlushBatch();
        await pumpTask;
    }

    private async Task WatchExitAsync(
        ManagedSession session,
        Func<KhEnvelope, Task> sendAsync,
        CancellationToken ct)
    {
        try
        {
            await session.Process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) { return; }

        var exitCode = session.Process.ExitCode;
        _registry.TryRemove(session.SessionId, out _);
        _logger.LogInformation("KH: Session {Id} exited (code {Code})", session.SessionId, exitCode);

        var report = new KhEnvelope
        {
            Type = KhMessageType.Report,
            SessionId = session.SessionId,
            Payload = JsonSerializer.SerializeToElement(
                new ReportPayload(exitCode, exitCode == 0 ? "exited" : "error"))
        };
        try { await sendAsync(report); } catch { /* WS may be closed */ }
        await session.DisposeAsync();
    }

    private static KhEnvelope BuildStreamOut(string sessionId, string fd, string[] lines) => new()
    {
        Type = KhMessageType.StreamOut,
        SessionId = sessionId,
        Payload = JsonSerializer.SerializeToElement(
            new StreamOutPayload(lines, fd, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
    };

    private static KhEnvelope SpawnAckEnvelope(string sessionId, Process proc, string command, string? cwd, string sourceSurface) => new()
    {
        Type = KhMessageType.SpawnAck,
        SessionId = sessionId,
        Payload = JsonSerializer.SerializeToElement(new SessionInfo(
            SessionId: sessionId,
            Pid: proc.Id,
            StartTime: DateTimeOffset.UtcNow.ToString("O"),
            Command: command,
            Cwd: cwd,
            SourceSurface: sourceSurface,
            Running: true))
    };

    private static KhEnvelope AckEnvelope(string? sessionId, bool ok, string? message = null) => new()
    {
        Type = KhMessageType.Ack,
        SessionId = sessionId,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(ok, message))
    };

    private static KhEnvelope ErrorEnvelope(string? sessionId, string message) => new()
    {
        Type = KhMessageType.Error,
        SessionId = sessionId,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(false, message))
    };

    private static string ResolveExecutable(string command)
    {
        // Strip args — caller should use payload.Args for arguments
        var exe = command.Split(' ', 2)[0].Trim('"', '\'');

        // Already a full path — return as-is
        if (Path.IsPathRooted(exe)) return exe;

        // Bare name — try to resolve to full path via PATH
        var resolved = FindOnPath(exe);
        return resolved ?? exe;
    }

    private static string? FindOnPath(string exe)
    {
        // Append .exe if missing
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exe += ".exe";

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
        foreach (var dir in paths)
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static HashSet<string> LoadAllowlist()
    {
        if (!File.Exists(AllowlistPath))
        {
            // Create default allowlist with common dev tools (full paths + bare names)
            var defaults = new[]
            {
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                @"C:\Windows\System32\cmd.exe",
                "powershell.exe",
                "pwsh.exe",
                "cmd.exe",
                "bash.exe",
                "wsl.exe",
                "codex.exe",
                "codex",
                "ghcp.exe",
                "ghcp",
                "inferno.exe",
                "inferno",
                "claude.exe",
                "claude",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(AllowlistPath)!);
            File.WriteAllLines(AllowlistPath, defaults);
            return new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
        }

        return File.ReadAllLines(AllowlistPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
