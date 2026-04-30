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

        if (IsReapplyRequest(payload))
        {
            var result = _tiling.ReapplyAliases();
            var message =
                $"Checked {result.Checked} grid window(s); reapplied {result.Renamed} stored name(s)" +
                (result.MissingAlias > 0 ? $"; {result.MissingAlias} window(s) have no stored name" : "") +
                (result.Failed > 0 ? $"; {result.Failed} failed" : "") +
                ".";
            await sendAsync(new KhEnvelope
            {
                Type = KhMessageType.Ack,
                SessionId = envelope.SessionId,
                Payload = JsonSerializer.SerializeToElement(new AckPayload(true, message)),
            });
            return;
        }

        var alias = payload.Alias ?? "";
        var ok =
            TryParseHandle(payload.Hwnd, out var hwnd)
                ? _tiling.RenameWindow(hwnd, alias)
                : TryParseHandle(payload.MonitorHandle, out var monitor) && payload.Slot is { } monitorSlot
                    ? _tiling.RenameSlot(monitor, monitorSlot, alias)
                : payload.Slot is { } fallbackSlot && _tiling.RenameSlot(fallbackSlot, alias);

        var target = payload.Hwnd is { Length: > 0 }
            ? $"window {payload.Hwnd}"
            : payload.MonitorHandle is { Length: > 0 } && payload.Slot is { } slot
                ? $"monitor {payload.MonitorHandle} slot {slot}"
                : payload.Slot is { } activeTargetSlot
                    ? $"slot {activeTargetSlot}"
                    : "unknown target";

        var ack = new KhEnvelope
        {
            Type = KhMessageType.Ack,
            SessionId = envelope.SessionId,
            Payload = JsonSerializer.SerializeToElement(new AckPayload(ok,
                ok ? $"{target} renamed to '{alias}'" : $"{target} not found")),
        };
        await sendAsync(ack);
    }

    private static bool IsReapplyRequest(RenamePayload payload)
    {
        var action = Normalize(payload.Action);
        if (action is "reapply" or "checknames" or "refresh" or "restore")
            return true;

        return string.IsNullOrWhiteSpace(payload.Alias) &&
               string.IsNullOrWhiteSpace(payload.Hwnd) &&
               string.IsNullOrWhiteSpace(payload.MonitorHandle) &&
               payload.Slot is null;
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static bool TryParseHandle(string? value, out nint handle)
    {
        handle = nint.Zero;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var style = System.Globalization.NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = System.Globalization.NumberStyles.HexNumber;
        }

        if (!long.TryParse(text, style, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;

        handle = new nint(parsed);
        return handle != nint.Zero;
    }

    private static KhEnvelope MakeError(KhEnvelope req, string message) => new()
    {
        Type = KhMessageType.Error,
        SessionId = req.SessionId,
        Payload = JsonSerializer.SerializeToElement(new AckPayload(false, message)),
    };
}
