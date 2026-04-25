using System.Text.Json;
using TinyBoss.Platform.Windows;
using TinyBoss.Protocol;

namespace TinyBoss.Handlers;

public sealed class RenameHandler
{
    private readonly TilingCoordinator _tiling;

    public RenameHandler(TilingCoordinator tiling) => _tiling = tiling;

    public async Task HandleAsync(KhEnvelope envelope, Func<KhEnvelope, Task> sendAsync, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<RenamePayload>(envelope.Payload.GetRawText());
        if (payload is null)
        {
            await sendAsync(MakeError(envelope, "Invalid rename payload"));
            return;
        }

        var ok = _tiling.RenameSlot(payload.Slot, payload.Alias);
        var ack = new KhEnvelope
        {
            Type = KhMessageType.Ack,
            SessionId = envelope.SessionId,
            Payload = JsonSerializer.SerializeToElement(new AckPayload(ok,
                ok ? $"Slot {payload.Slot} renamed to '{payload.Alias}'" : $"Slot {payload.Slot} not found")),
        };
        await sendAsync(ack);
    }

    private static KhEnvelope MakeError(KhEnvelope req, string message) => new()
    {
        Type = KhMessageType.Error,
        SessionId = req.SessionId,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(false, message)),
    };
}
