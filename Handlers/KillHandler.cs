using System.Text.Json;
using KittenHerder.Core;
using KittenHerder.Protocol;

namespace KittenHerder.Handlers;

public sealed class KillHandler
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<KillHandler> _logger;

    public KillHandler(SessionRegistry registry, ILogger<KillHandler> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task HandleAsync(KhEnvelope envelope, Func<KhEnvelope, Task> sendAsync, CancellationToken ct)
    {
        var session = _registry.Get(envelope.SessionId ?? "");
        if (session is null)
        {
            await sendAsync(Error(envelope.SessionId, "Session not found"));
            return;
        }

        try
        {
            if (!session.Process.HasExited)
                session.Process.Kill(entireProcessTree: true);

            _registry.TryRemove(session.SessionId, out _);
            _logger.LogInformation("KH: Killed session {Id} (pid {Pid})", session.SessionId, session.Key.Pid);
            await sendAsync(Ack(envelope.SessionId));
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Kill failed for session {Id}", session.SessionId);
            await sendAsync(Error(envelope.SessionId, ex.Message));
        }
    }

    private static KhEnvelope Ack(string? id) => new()
    {
        Type = KhMessageType.Ack, SessionId = id,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(true))
    };

    private static KhEnvelope Error(string? id, string msg) => new()
    {
        Type = KhMessageType.Error, SessionId = id,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(false, msg))
    };
}
