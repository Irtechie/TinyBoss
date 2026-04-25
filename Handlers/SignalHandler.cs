using System.Text.Json;
using KittenHerder.Core;
using KittenHerder.Protocol;

namespace KittenHerder.Handlers;

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

        try
        {
            // Write Ctrl+C character to stdin — works for console apps reading stdin
            var sigChar = signal == "ctrl_break" ? "\x1c" : "\x03";
            await session.Process.StandardInput.WriteAsync(sigChar.AsMemory(), ct);
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
