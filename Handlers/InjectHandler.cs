using System.Net.WebSockets;
using System.Text.Json;
using TinyBoss.Core;
using TinyBoss.Protocol;

namespace TinyBoss.Handlers;

/// <summary>
/// Injects text into a running session's stdin.
/// SECURITY: Blocked for discord-surface sessions (prompt injection fence).
/// This is enforced at KH, not at the PitBoss layer.
/// </summary>
public sealed class InjectHandler
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<InjectHandler> _logger;

    public InjectHandler(SessionRegistry registry, ILogger<InjectHandler> logger)
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

        // Prompt injection fence — Discord surface may not inject stdin
        if (session.SourceSurface.Equals("discord", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("KH: Inject blocked for discord-surface session {Id}", session.SessionId);
            await sendAsync(Error(envelope.SessionId, "Inject not permitted for discord-surface sessions"));
            return;
        }

        if (!session.SupportsCapability(TinyBossCapability.SendText))
        {
            _logger.LogWarning(
                "KH: Inject blocked for session {Id}; kind={Kind} capabilities={Capabilities}",
                session.SessionId,
                session.SessionKind,
                string.Join(",", session.Capabilities));
            await sendAsync(Error(envelope.SessionId, $"Session kind '{session.SessionKind}' does not support send_text"));
            return;
        }

        var payload = envelope.Payload.Deserialize<InjectPayload>();
        if (payload is null || string.IsNullOrEmpty(payload.Text))
        {
            await sendAsync(Error(envelope.SessionId, "Invalid inject payload"));
            return;
        }

        try
        {
            await session.WriteInputAsync(payload.Text.AsMemory(), ct);
            _logger.LogDebug("KH: Injected {N} chars into session {Id}", payload.Text.Length, session.SessionId);
            await sendAsync(Ack(envelope.SessionId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Inject failed for session {Id}", session.SessionId);
            await sendAsync(Error(envelope.SessionId, ex.Message));
        }
    }

    private static KhEnvelope Ack(string? sessionId) => new()
    {
        Type = KhMessageType.Ack,
        SessionId = sessionId,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(true))
    };

    private static KhEnvelope Error(string? sessionId, string message) => new()
    {
        Type = KhMessageType.Error,
        SessionId = sessionId,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(false, message))
    };
}
