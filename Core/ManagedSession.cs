using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace TinyBoss.Core;

/// <summary>
/// Composite key that prevents PID-recycling false positives.
/// </summary>
public readonly record struct SessionKey(int Pid, DateTimeOffset StartTime);

/// <summary>
/// A live process managed by TinyBoss.
/// Owns the Process, stdout Channel, and surface origin tracking.
/// </summary>
public sealed class ManagedSession : IAsyncDisposable
{
    public string SessionId { get; }
    public string Command { get; }
    public string? Cwd { get; }
    public string SourceSurface { get; }
    public Process Process { get; }
    public SessionKey Key { get; }
    public string SessionKind { get; }
    public string[] Capabilities { get; }
    public bool RawStreamAvailable { get; }
    public bool SupportsTranscriptTail { get; }
    public ITerminalSessionBackend? TerminalBackend { get; }
    public TerminalTranscriptBuffer TranscriptBuffer { get; } = new();
    public DateTimeOffset? LastOutputAt { get; private set; }

    /// <summary>Bounded stdout queue — producer: ReadLineAsync loop; consumer: sender task.</summary>
    private readonly Channel<string> _stdoutChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    /// <summary>Bounded stderr queue.</summary>
    private readonly Channel<string> _stderrChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private int _droppedLines;

    public ChannelReader<string> StdoutReader => _stdoutChannel.Reader;
    public ChannelReader<string> StderrReader => _stderrChannel.Reader;

    public ManagedSession(
        string sessionId,
        string command,
        string? cwd,
        string sourceSurface,
        Process process,
        string sessionKind = TinyBossSessionKind.RedirectedProcess,
        ITerminalSessionBackend? terminalBackend = null)
    {
        SessionId = sessionId;
        Command = command;
        Cwd = cwd;
        SourceSurface = sourceSurface;
        Process = process;
        Key = new SessionKey(process.Id, process.StartTime.ToUniversalTime());
        SessionKind = sessionKind;
        TerminalBackend = terminalBackend;
        Capabilities = CapabilitiesFor(sessionKind);
        RawStreamAvailable = terminalBackend?.RawStreamAvailable
            ?? sessionKind is TinyBossSessionKind.RedirectedProcess or TinyBossSessionKind.BossTerminalPty;
        SupportsTranscriptTail = RawStreamAvailable;
    }

    public string State => Process.HasExited ? TinyBossLaneState.Exited : TinyBossLaneState.Running;

    public int? ExitCode
    {
        get
        {
            try
            {
                return Process.HasExited ? Process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool SupportsCapability(string capability) =>
        Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);

    private static string[] CapabilitiesFor(string sessionKind) => sessionKind switch
    {
        TinyBossSessionKind.BossTerminalPty =>
        [
            TinyBossCapability.SendText,
            TinyBossCapability.StreamTranscript,
            TinyBossCapability.Interrupt,
            TinyBossCapability.Kill,
            TinyBossCapability.Resize,
            TinyBossCapability.SetTitle
        ],
        TinyBossSessionKind.RedirectedProcess =>
        [
            TinyBossCapability.SendText,
            TinyBossCapability.StreamTranscript,
            TinyBossCapability.Interrupt,
            TinyBossCapability.Kill
        ],
        _ =>
        [
            TinyBossCapability.Kill,
            TinyBossCapability.SetTitle
        ]
    };

    /// <summary>
    /// Pump stdout into the bounded channel. Call after process.Start().
    /// Exits when stdout stream closes (process exited).
    /// </summary>
    public Task PumpStdoutAsync(CancellationToken ct) =>
        TerminalBackend is not null
            ? PumpTerminalOutputAsync(ct)
            : PumpStreamAsync(Process.StandardOutput, _stdoutChannel, ct);

    /// <summary>
    /// Pump stderr into the bounded channel. Call after process.Start().
    /// </summary>
    public Task PumpStderrAsync(CancellationToken ct)
    {
        if (TerminalBackend is not null)
        {
            _stderrChannel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        return PumpStreamAsync(Process.StandardError, _stderrChannel, ct);
    }

    public Task WriteInputAsync(ReadOnlyMemory<char> text, CancellationToken ct) =>
        TerminalBackend is not null
            ? TerminalBackend.WriteInputAsync(text, ct)
            : Process.StandardInput.WriteAsync(text, ct);

    public Task WriteSignalAsync(string signal, CancellationToken ct)
    {
        if (TerminalBackend is not null)
            return TerminalBackend.WriteSignalAsync(signal, ct);

        var sigChar = signal == "ctrl_break" ? "\x1c" : "\x03";
        return Process.StandardInput.WriteAsync(sigChar.AsMemory(), ct);
    }

    public void Kill()
    {
        if (TerminalBackend is not null)
        {
            TerminalBackend.Kill();
            return;
        }

        if (!Process.HasExited)
            Process.Kill(entireProcessTree: true);
    }

    private async Task PumpTerminalOutputAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in TerminalBackend!.ReadOutputAsync(ct))
            {
                WriteChannel(_stdoutChannel, chunk);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _stdoutChannel.Writer.TryComplete();
        }
    }

    private async Task PumpStreamAsync(StreamReader reader, Channel<string> channel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;   // EOF

                WriteChannel(channel, line);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private void WriteChannel(Channel<string> channel, string text)
    {
        LastOutputAt = DateTimeOffset.UtcNow;
        TranscriptBuffer.AddChunk(text);
        if (!channel.Writer.TryWrite(text))
        {
            var dropped = Interlocked.Increment(ref _droppedLines);
            channel.Writer.TryWrite($"[KH: {dropped} chunks dropped due to backpressure]");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stdoutChannel.Writer.TryComplete();
        _stderrChannel.Writer.TryComplete();
        if (TerminalBackend is not null)
        {
            await TerminalBackend.DisposeAsync();
            return;
        }

        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch { }
        await Process.WaitForExitAsync();
        Process.Dispose();
    }
}
