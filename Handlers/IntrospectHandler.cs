using System.Text.Json;
using TinyBoss.Core;
using TinyBoss.Platform.Windows;
using TinyBoss.Protocol;

namespace TinyBoss.Handlers;

public sealed class IntrospectHandler
{
    private readonly SessionRegistry _registry;
    private readonly TilingCoordinator _tiling;

    public IntrospectHandler(SessionRegistry registry, TilingCoordinator tiling)
    {
        _registry = registry;
        _tiling = tiling;
    }

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

        var windows = _tiling.GetAllSnapshots()
            .SelectMany(snapshot => snapshot.Slots
                .OrderBy(kv => kv.Key)
                .Select(kv =>
                {
                    var tile = kv.Value;
                    var textTail = WindowTextCapture.CaptureTail(tile.Hwnd);
                    return new TiledWindowInfo(
                        DeviceName:    snapshot.DeviceName,
                        MonitorHandle: snapshot.MonitorHandle.ToString(),
                        Slot:          kv.Key,
                        Hwnd:          tile.Hwnd.ToString(),
                        Pid:           tile.ProcessId,
                        SessionId:     tile.SessionId,
                        Alias:         tile.Alias,
                        Title:         TilingCoordinator.GetWindowTitle(tile.Hwnd),
                        Running:       TilingCoordinator.IsWindow(tile.Hwnd),
                        TextTail:      textTail,
                        CapturedAt:    textTail.Length > 0 ? DateTimeOffset.UtcNow.ToString("O") : null);
                }))
            .ToArray();

        var reply = new KhEnvelope
        {
            Type      = KhMessageType.IntrospectReply,
            SessionId = null,
            Payload   = JsonSerializer.SerializeToElement(new IntrospectReplyPayload(sessions, windows))
        };
        await sendAsync(reply);
    }
}
