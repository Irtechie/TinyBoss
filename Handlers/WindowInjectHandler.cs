using System.Text.Json;
using TinyBoss.Platform.Windows;
using TinyBoss.Protocol;
using TinyBoss.Voice;

namespace TinyBoss.Handlers;

/// <summary>
/// Injects text into a tiled visible terminal by HWND or grid slot.
/// This is the bridge PitBoss YOLO uses for windows that TinyBoss can see
/// but did not originally spawn as managed sessions.
/// </summary>
public sealed class WindowInjectHandler
{
    private readonly TilingCoordinator _tiling;
    private readonly TextInjector _injector;

    public WindowInjectHandler(TilingCoordinator tiling, TextInjector injector)
    {
        _tiling = tiling;
        _injector = injector;
    }

    public async Task HandleAsync(KhEnvelope envelope, Func<KhEnvelope, Task> sendAsync, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<WindowInjectPayload>(envelope.Payload.GetRawText());
        if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        {
            await sendAsync(MakeError(envelope, "Invalid window_inject payload"));
            return;
        }

        var hwnd = ResolveHwnd(payload);
        if (hwnd == nint.Zero)
        {
            await sendAsync(MakeError(envelope, "Target window not found"));
            return;
        }

        var result = await _injector.InjectWindowAsync(payload.Text, hwnd, ct);
        await sendAsync(new KhEnvelope
        {
            Type = result.Success ? KhMessageType.Ack : KhMessageType.Error,
            SessionId = envelope.SessionId,
            Payload = JsonSerializer.SerializeToElement(new AckPayload(result.Success, result.Message)),
        });
    }

    private nint ResolveHwnd(WindowInjectPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Hwnd) && TryParseHwnd(payload.Hwnd, out var hwnd))
            return hwnd;

        if (payload.Slot is null)
            return nint.Zero;

        var snapshots = _tiling.GetAllSnapshots();
        if (!string.IsNullOrWhiteSpace(payload.MonitorHandle))
        {
            snapshots = snapshots
                .Where(snapshot => snapshot.MonitorHandle.ToString()
                    .Equals(payload.MonitorHandle, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Slots.TryGetValue(payload.Slot.Value, out var tile))
                return tile.Hwnd;
        }

        return nint.Zero;
    }

    private static bool TryParseHwnd(string value, out nint hwnd)
    {
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            hwnd = (nint)hex;
            return true;
        }

        if (long.TryParse(text, out var dec))
        {
            hwnd = (nint)dec;
            return true;
        }

        hwnd = nint.Zero;
        return false;
    }

    private static KhEnvelope MakeError(KhEnvelope req, string message) => new()
    {
        Type = KhMessageType.Error,
        SessionId = req.SessionId,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(false, message)),
    };
}
