using System.Text.Json;
using TinyBoss.Core;
using TinyBoss.Protocol;

namespace TinyBoss.Handlers;

public sealed class IntrospectHandler
{
    private readonly SessionRegistry _registry;

    public IntrospectHandler(SessionRegistry registry) => _registry = registry;

    public async Task HandleAsync(KhEnvelope envelope, Func<KhEnvelope, Task> sendAsync, CancellationToken ct)
    {
        var sessions = _registry.All().Select(s => new SessionInfo(
            SessionId:     s.SessionId,
            Pid:           s.Key.Pid,
            StartTime:     s.Key.StartTime.ToString("O"),
            Command:       s.Command,
            Cwd:           s.Cwd,
            SourceSurface: s.SourceSurface,
            Running:       !s.Process.HasExited
        )).ToArray();

        var reply = new KhEnvelope
        {
            Type      = KhMessageType.IntrospectReply,
            SessionId = null,
            Payload   = JsonSerializer.SerializeToElement(new IntrospectReplyPayload(sessions))
        };
        await sendAsync(reply);
    }
}
