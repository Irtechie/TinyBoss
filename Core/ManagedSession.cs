using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace KittenHerder.Core;

/// <summary>
/// Composite key that prevents PID-recycling false positives.
/// </summary>
public readonly record struct SessionKey(int Pid, DateTimeOffset StartTime);

/// <summary>
/// A live process managed by KittenHerder.
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

    public ManagedSession(string sessionId, string command, string? cwd, string sourceSurface, Process process)
    {
        SessionId = sessionId;
        Command = command;
        Cwd = cwd;
        SourceSurface = sourceSurface;
        Process = process;
        Key = new SessionKey(process.Id, process.StartTime.ToUniversalTime());
    }

    /// <summary>
    /// Pump stdout into the bounded channel. Call after process.Start().
    /// Exits when stdout stream closes (process exited).
    /// </summary>
    public Task PumpStdoutAsync(CancellationToken ct) =>
        PumpStreamAsync(Process.StandardOutput, _stdoutChannel, ct);

    /// <summary>
    /// Pump stderr into the bounded channel. Call after process.Start().
    /// </summary>
    public Task PumpStderrAsync(CancellationToken ct) =>
        PumpStreamAsync(Process.StandardError, _stderrChannel, ct);

    private async Task PumpStreamAsync(StreamReader reader, Channel<string> channel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;   // EOF

                if (!channel.Writer.TryWrite(line))
                {
                    var dropped = Interlocked.Increment(ref _droppedLines);
                    channel.Writer.TryWrite($"[KH: {dropped} lines dropped due to backpressure]");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stdoutChannel.Writer.TryComplete();
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
