using System.Text.Json;
using TinyBoss.Core;
using TinyBoss.Protocol;

namespace TinyBoss.Handlers;

/// <summary>
/// Delivers a user's answer to a pending ask_user question in an Inferno pipeline session.
///
/// Security model:
///   - Only permitted for sessions whose SessionId starts with "inferno-"
///   - This is a SEPARATE message type from "inject" — it bypasses the discord surface
///     fence intentionally, because structured answers to known questions are safe.
///   - Terminal sessions (powershell, cmd, etc.) cannot receive answer_user messages.
///
/// Protocol:
///   PitBoss → KH: {type:"answer_user", session_id:"inferno-...", payload:{text:"proceed"}}
///   KH → Inferno stdin: "proceed\n"
///   Inferno AskUserTool.readline() unblocks → returns {"answer":"proceed"}
/// </summary>
public sealed class AnswerUserHandler
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<AnswerUserHandler> _logger;

    public AnswerUserHandler(SessionRegistry registry, ILogger<AnswerUserHandler> logger)
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

        // Only inferno sessions — validated by session ID prefix convention
        if (!session.SessionId.StartsWith("inferno-", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "KH: answer_user blocked for non-inferno session {Id} (surface={Surface})",
                session.SessionId, session.SourceSurface);
            await sendAsync(Error(envelope.SessionId, "answer_user only permitted for inferno sessions"));
            return;
        }

        var payload = envelope.Payload.Deserialize<AnswerUserPayload>();
        if (payload is null || string.IsNullOrEmpty(payload.Text))
        {
            await sendAsync(Error(envelope.SessionId, "Invalid answer_user payload: text is required"));
            return;
        }

        try
        {
            // Write answer + newline — AskUserTool.readline() in Inferno unblocks
            await session.Process.StandardInput.WriteAsync((payload.Text + "\n").AsMemory(), ct);
            _logger.LogDebug(
                "KH: Delivered answer ({N} chars) to inferno session {Id}",
                payload.Text.Length, session.SessionId);
            await sendAsync(Ack(envelope.SessionId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: answer_user stdin write failed for session {Id}", session.SessionId);
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
