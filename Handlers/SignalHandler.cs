using System.Text.Json;
using TinyBoss.Core;
using TinyBoss.Protocol;

namespace TinyBoss.Handlers;

/// <summary>Sends Ctrl+C or Ctrl+Break to a running session.</summary>
public sealed class SignalHandler
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<SignalHandler> _logger;

    public SignalHandler(SessionRegistry registry, ILogger<SignalHandler> logger)
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

        var payload = envelope.Payload.Deserialize<SignalPayload>();
        var signal = payload?.Signal ?? "ctrl_c";

        if (!session.SupportsCapability(TinyBossCapability.Interrupt))
        {
            _logger.LogWarning(
                "KH: Signal {Sig} blocked for session {Id}; kind={Kind}",
                signal,
                session.SessionId,
                session.SessionKind);
            await sendAsync(Error(envelope.SessionId, $"Session kind '{session.SessionKind}' does not support interrupt"));
            return;
        }

        try
        {
            await session.WriteSignalAsync(signal, ct);
            _logger.LogInformation("KH: Signal {Sig} → session {Id}", signal, session.SessionId);
            await sendAsync(Ack(envelope.SessionId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Signal failed for session {Id}", session.SessionId);
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
