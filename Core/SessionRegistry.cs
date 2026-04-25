using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace KittenHerder.Core;

/// <summary>
/// Tracks all active ManagedSessions. Persists to disk on every mutation.
/// On startup, validates persisted sessions against live processes.
/// Fires <see cref="SessionChanged"/> on add/remove so the tray UI can refresh.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, ManagedSession> _byId = new();
    private readonly ILogger<SessionRegistry> _logger;
    private readonly string _persistPath;
    private readonly string _persistDir;

    /// <summary>Fires on session add/remove. Subscribers must marshal to UI thread.</summary>
    public event Action<SessionEvent>? SessionChanged;

    public SessionRegistry(ILogger<SessionRegistry> logger)
    {
        _logger = logger;
        _persistDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KittenHerder");
        _persistPath = Path.Combine(_persistDir, "sessions.json");
        Directory.CreateDirectory(_persistDir);
        ValidatePersistedSessions();
    }

    public bool TryAdd(ManagedSession session)
    {
        if (!_byId.TryAdd(session.SessionId, session))
            return false;
        Persist();
        SessionChanged?.Invoke(new SessionEvent(SessionEventKind.Added, session));
        return true;
    }

    public bool TryRemove(string sessionId, out ManagedSession? session)
    {
        if (!_byId.TryRemove(sessionId, out session))
            return false;
        Persist();
        SessionChanged?.Invoke(new SessionEvent(SessionEventKind.Removed, session));
        return true;
    }

    public ManagedSession? Get(string sessionId) =>
        _byId.TryGetValue(sessionId, out var s) ? s : null;

    /// <summary>Thread-safe snapshot for UI reads.</summary>
    public IReadOnlyList<ManagedSession> GetSnapshot() => _byId.Values.ToArray();

    public IEnumerable<ManagedSession> All() => _byId.Values;

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Persist()
    {
        try
        {
            var entries = _byId.Values.Select(s => new PersistedSession(
                s.SessionId, s.Key.Pid, s.Key.StartTime.ToString("O"),
                s.Command, s.Cwd, s.SourceSurface
            )).ToArray();

            var json = JsonSerializer.Serialize(entries);
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Failed to persist session registry");
        }
    }

    private void ValidatePersistedSessions()
    {
        if (!File.Exists(_persistPath)) return;
        try
        {
            var json = File.ReadAllText(_persistPath);
            var entries = JsonSerializer.Deserialize<PersistedSession[]>(json);
            if (entries is null) return;

            int orphaned = 0;
            foreach (var e in entries)
            {
                if (!DateTimeOffset.TryParse(e.StartTimeIso, out var startTime)) continue;
                try
                {
                    var proc = Process.GetProcessById(e.Pid);
                    var actualStart = proc.StartTime.ToUniversalTime();
                    if (Math.Abs((actualStart - startTime.UtcDateTime).TotalSeconds) < 2)
                    {
                        // Process is still the same one — mark as orphaned (Logos reconnect will introspect)
                        _logger.LogInformation("KH: Orphaned session {Id} (pid {Pid}) recovered", e.SessionId, e.Pid);
                    }
                    else
                    {
                        orphaned++;
                    }
                }
                catch
                {
                    orphaned++;   // process gone
                }
            }
            if (orphaned > 0)
                _logger.LogInformation("KH: {N} stale sessions cleared from registry", orphaned);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Could not load persisted sessions — starting fresh");
        }
    }
}

internal sealed record PersistedSession(
    string SessionId, int Pid, string StartTimeIso,
    string Command, string? Cwd, string SourceSurface
);

public enum SessionEventKind { Added, Removed }
public sealed record SessionEvent(SessionEventKind Kind, ManagedSession Session);
