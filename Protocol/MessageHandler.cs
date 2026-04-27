using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinyBoss.Core;
using TinyBoss.Handlers;
using TinyBoss.Protocol;

namespace TinyBoss.Protocol;

/// <summary>
/// Dispatches inbound KhEnvelopes to the appropriate handler.
/// One instance per WebSocket connection (there is only one — Logos).
/// </summary>
public sealed class MessageHandler
{
    private readonly SpawnHandler _spawn;
    private readonly InjectHandler _inject;
    private readonly WindowInjectHandler _windowInject;
    private readonly KillHandler _kill;
    private readonly IntrospectHandler _introspect;
    private readonly SignalHandler _signal;
    private readonly AnswerUserHandler _answerUser;
    private readonly RenameHandler _rename;
    private readonly ILogger<MessageHandler> _logger;
    private readonly string _authToken;

    // Dedicated sender channel — one drain task writes to WS (no concurrent sends)
    private readonly System.Threading.Channels.Channel<byte[]> _sendChannel =
        System.Threading.Channels.Channel.CreateBounded<byte[]>(512);

    public MessageHandler(
        SpawnHandler spawn, InjectHandler inject, WindowInjectHandler windowInject, KillHandler kill,
        IntrospectHandler introspect, SignalHandler signal, AnswerUserHandler answerUser,
        RenameHandler rename,
        ILogger<MessageHandler> logger, string authToken)
    {
        _spawn = spawn;
        _inject = inject;
        _windowInject = windowInject;
        _kill = kill;
        _introspect = introspect;
        _signal = signal;
        _answerUser = answerUser;
        _rename = rename;
        _logger = logger;
        _authToken = authToken;
    }

    /// <summary>
    /// Run the full connection lifecycle:
    /// Hello handshake → receive loop + sender task.
    /// Returns when connection closes or ct fires.
    /// </summary>
    public async Task RunConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Start dedicated sender task
        var senderTask = Task.Run(() => SenderLoopAsync(ws, connCts.Token), connCts.Token);

        try
        {
            // Hello handshake with 5s timeout
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token);
            helloCts.CancelAfter(TimeSpan.FromSeconds(5));
            if (!await PerformHandshakeAsync(ws, helloCts.Token))
            {
                _logger.LogWarning("KH: Handshake failed — closing connection");
                return;
            }

            await ReceiveLoopAsync(ws, connCts.Token);
        }
        catch (WebSocketException ex) when (
            ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely ||
            ex.WebSocketErrorCode == WebSocketError.InvalidState)
        {
            _logger.LogWarning("KH: Client disconnected abruptly (no close handshake)");
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        finally
        {
            _sendChannel.Writer.TryComplete();
            await connCts.CancelAsync();
            await senderTask.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private async Task<bool> PerformHandshakeAsync(WebSocket ws, CancellationToken ct)
    {
        var envelope = await ReceiveEnvelopeAsync(ws, ct);
        if (envelope is null || envelope.Type != KhMessageType.Hello) return false;

        var token = envelope.Payload.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (!ValidateToken(token))
        {
            _logger.LogWarning("KH: Invalid auth token in Hello");
            return false;
        }

        var ack = new KhEnvelope
        {
            Type = KhMessageType.HelloAck,
            Payload = JsonSerializer.SerializeToElement(new AckPayload(true, "TinyBoss ready"))
        };
        await EnqueueSendAsync(ack, ct);
        _logger.LogInformation("KH: PitBoss connected and authenticated");
        return true;
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var envelope = await ReceiveEnvelopeAsync(ws, ct);
            if (envelope is null) break;

            Func<KhEnvelope, Task> send = e => EnqueueSendAsync(e, ct);

            try
            {
                switch (envelope.Type)
                {
                    case KhMessageType.Spawn:
                        await _spawn.HandleAsync(envelope, ws, send, ct);
                        break;
                    case KhMessageType.Inject:
                        await _inject.HandleAsync(envelope, send, ct);
                        break;
                    case KhMessageType.WindowInject:
                        await _windowInject.HandleAsync(envelope, send, ct);
                        break;
                    case KhMessageType.Kill:
                        await _kill.HandleAsync(envelope, send, ct);
                        break;
                    case KhMessageType.Introspect:
                        await _introspect.HandleAsync(envelope, send, ct);
                        break;
                    case KhMessageType.Signal:
                        await _signal.HandleAsync(envelope, send, ct);
                        break;
                    case KhMessageType.AnswerUser:
                        await _answerUser.HandleAsync(envelope, send, ct);
                        break;
                    case KhMessageType.Rename:
                        await _rename.HandleAsync(envelope, send, ct);
                        break;
                    default:
                        _logger.LogDebug("KH: Unknown message type: {Type}", envelope.Type);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KH: Handler error for type {Type}", envelope.Type);
            }
        }
    }

    private async Task SenderLoopAsync(WebSocket ws, CancellationToken ct)
    {
        await foreach (var frame in _sendChannel.Reader.ReadAllAsync(ct))
        {
            if (ws.State != WebSocketState.Open) break;
            try
            {
                await ws.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("KH: Send failed: {Msg}", ex.Message);
                break;
            }
        }
    }

    private Task EnqueueSendAsync(KhEnvelope envelope, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        return _sendChannel.Writer.WriteAsync(bytes, ct).AsTask();
    }

    private static async Task<KhEnvelope?> ReceiveEnvelopeAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonSerializer.Deserialize<KhEnvelope>(ms.ToArray());
    }

    private bool ValidateToken(string? provided)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(_authToken))
            return string.IsNullOrEmpty(_authToken);   // open if no token configured (dev mode)

        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(_authToken);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
