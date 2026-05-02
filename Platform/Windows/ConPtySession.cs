using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TinyBoss.Core;

namespace TinyBoss.Platform.Windows;

public sealed class ConPtySession : ITerminalSessionBackend
{
    private readonly nint _pseudoConsole;
    private readonly SafeFileHandle _inputReadSide;
    private readonly SafeFileHandle _inputHandle;
    private readonly SafeFileHandle _outputWriteSide;
    private readonly FileStream _output;
    private readonly nint _processHandle;
    private readonly nint _threadHandle;
    private readonly ILogger? _logger;
    private bool _disposed;

    public Process Process { get; }
    public bool RawStreamAvailable => true;

    private ConPtySession(
        Process process,
        nint pseudoConsole,
        SafeFileHandle inputReadSide,
        SafeFileHandle inputHandle,
        SafeFileHandle outputWriteSide,
        FileStream output,
        nint processHandle,
        nint threadHandle,
        ILogger? logger)
    {
        Process = process;
        _pseudoConsole = pseudoConsole;
        _inputReadSide = inputReadSide;
        _inputHandle = inputHandle;
        _outputWriteSide = outputWriteSide;
        _output = output;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        _logger = logger;
    }

    public static ConPtySession Start(
        string executable,
        IReadOnlyList<string>? args,
        string? workingDirectory,
        ILogger? logger = null,
        short columns = 120,
        short rows = 30)
    {
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        nint pseudoConsole = 0;
        nint attributeList = 0;

        try
        {
            if (!ConPtyNative.CreatePipe(out inputRead, out inputWrite, 0, 0))
                throw ConPtyNative.LastWin32Exception();

            if (!ConPtyNative.CreatePipe(out outputRead, out outputWrite, 0, 0))
                throw ConPtyNative.LastWin32Exception();

            var hr = ConPtyNative.CreatePseudoConsole(
                new ConPtyNative.COORD(columns, rows),
                inputRead,
                outputWrite,
                0,
                out pseudoConsole);
            if (hr != 0)
                throw ConPtyNative.HResultException(hr, "CreatePseudoConsole");

            attributeList = AllocateAttributeList(pseudoConsole);
            var startupInfo = new ConPtyNative.STARTUPINFOEX
            {
                StartupInfo = { cb = Marshal.SizeOf<ConPtyNative.STARTUPINFOEX>() },
                lpAttributeList = attributeList
            };

            var commandLine = BuildCommandLine(executable, args);
            var processSecurity = new ConPtyNative.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>()
            };
            var threadSecurity = new ConPtyNative.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>()
            };
            if (!ConPtyNative.CreateProcessW(
                    null,
                    commandLine,
                    ref processSecurity,
                    ref threadSecurity,
                    false,
                    ConPtyNative.EXTENDED_STARTUPINFO_PRESENT,
                    0,
                    string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                    ref startupInfo,
                    out var processInfo))
            {
                throw ConPtyNative.LastWin32Exception();
            }

            var inputReadSide = inputRead;
            inputRead = null;

            var input = inputWrite;
            inputWrite = null;

            var outputWriteSide = outputWrite;
            outputWrite = null;

            var output = new FileStream(
                outputRead,
                FileAccess.Read,
                bufferSize: 8192,
                isAsync: false);
            outputRead = null;

            var process = Process.GetProcessById(processInfo.dwProcessId);
            return new ConPtySession(
                process,
                pseudoConsole,
                inputReadSide,
                input,
                outputWriteSide,
                output,
                processInfo.hProcess,
                processInfo.hThread,
                logger);
        }
        catch
        {
            SafeDispose(inputRead);
            SafeDispose(inputWrite);
            SafeDispose(outputRead);
            SafeDispose(outputWrite);
            if (pseudoConsole != 0)
                ConPtyNative.ClosePseudoConsole(pseudoConsole);
            throw;
        }
        finally
        {
            if (attributeList != 0)
            {
                ConPtyNative.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    public async IAsyncEnumerable<string> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _output.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            if (read <= 0)
                yield break;

            var charCount = decoder.GetChars(buffer.AsSpan(0, read), chars, flush: false);
            if (charCount > 0)
                yield return new string(chars, 0, charCount);
        }
    }

    public async Task WriteInputAsync(ReadOnlyMemory<char> text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(text.Span.ToString());
        await Task.Run(() =>
        {
            if (!ConPtyNative.WriteFile(
                    _inputHandle.DangerousGetHandle(),
                    bytes,
                    checked((uint)bytes.Length),
                    out var written,
                    0))
            {
                throw ConPtyNative.LastWin32Exception();
            }

            if (written != bytes.Length)
                throw new IOException($"ConPTY input wrote {written} of {bytes.Length} bytes.");
        }, ct);
    }

    public Task WriteSignalAsync(string signal, CancellationToken ct)
    {
        var sigChar = signal.Equals("ctrl_break", StringComparison.OrdinalIgnoreCase) ? "\x1c" : "\x03";
        return WriteInputAsync(sigChar.AsMemory(), ct);
    }

    public Task ResizeAsync(short columns, short rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var hr = ConPtyNative.ResizePseudoConsole(
            _pseudoConsole,
            new ConPtyNative.COORD(Math.Max((short)1, columns), Math.Max((short)1, rows)));
        if (hr != 0)
            throw ConPtyNative.HResultException(hr, "ResizePseudoConsole");
        return Task.CompletedTask;
    }

    public void Kill()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "TinyBoss ConPTY process kill failed for PID {Pid}", Process.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _inputReadSide.Dispose(); } catch { }
        try { _inputHandle.Dispose(); } catch { }
        try { _outputWriteSide.Dispose(); } catch { }
        try { _output.Dispose(); } catch { }

        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch { }

        try
        {
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { }

        try { ConPtyNative.ClosePseudoConsole(_pseudoConsole); } catch { }
        try { ConPtyNative.CloseHandle(_threadHandle); } catch { }
        try { ConPtyNative.CloseHandle(_processHandle); } catch { }
        Process.Dispose();
    }

    private static nint AllocateAttributeList(nint pseudoConsole)
    {
        nint attributeListSize = 0;
        ConPtyNative.InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
        if (attributeListSize == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        try
        {
            if (!ConPtyNative.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw ConPtyNative.LastWin32Exception();

            if (!ConPtyNative.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (nint)ConPtyNative.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole,
                    (nint)nint.Size,
                    0,
                    0))
            {
                throw ConPtyNative.LastWin32Exception();
            }

            return attributeList;
        }
        catch
        {
            ConPtyNative.DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw;
        }
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string>? args)
    {
        var parts = new List<string> { QuoteArgument(executable, alwaysQuote: true) };
        if (args is not null)
            parts.AddRange(args.Select(arg => QuoteArgument(arg)));
        return string.Join(" ", parts);
    }

    private static string QuoteArgument(string value, bool alwaysQuote = false)
    {
        if (value.Length == 0)
            return "\"\"";

        if (!alwaysQuote && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(ch);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void SafeDispose(SafeFileHandle? handle)
    {
        try { handle?.Dispose(); } catch { }
    }
}
