using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyBoss.Platform.Windows;

public static class ProcessWorkingDirectory
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const int ProcessBasicInformation = 0;

    public static string? TryGet(int pid)
    {
        if (!Environment.Is64BitProcess)
            return null;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == nint.Zero)
            return null;

        try
        {
            var pbi = new ProcessBasicInformation64();
            var status = NtQueryInformationProcess(
                handle,
                ProcessBasicInformation,
                ref pbi,
                Marshal.SizeOf<ProcessBasicInformation64>(),
                out _);
            if (status != 0 || pbi.PebBaseAddress == 0)
                return null;

            var processParameters = ReadIntPtr64(handle, (nint)(pbi.PebBaseAddress + 0x20));
            if (processParameters == nint.Zero)
                return null;

            var currentDirectory = ReadUnicodeString64(handle, processParameters + 0x38);
            if (string.IsNullOrWhiteSpace(currentDirectory))
                return null;

            return Directory.Exists(currentDirectory) ? currentDirectory : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? TryGet(Process process)
    {
        try { return TryGet(process.Id); }
        catch { return null; }
    }

    private static nint ReadIntPtr64(nint processHandle, nint address)
    {
        var buffer = new byte[8];
        return ReadProcessMemory(processHandle, address, buffer, buffer.Length, out var read) && read == buffer.Length
            ? (nint)BitConverter.ToInt64(buffer, 0)
            : nint.Zero;
    }

    private static string? ReadUnicodeString64(nint processHandle, nint address)
    {
        var buffer = new byte[16];
        if (!ReadProcessMemory(processHandle, address, buffer, buffer.Length, out var read) || read < buffer.Length)
            return null;

        var length = BitConverter.ToUInt16(buffer, 0);
        var maximumLength = BitConverter.ToUInt16(buffer, 2);
        var stringBuffer = (nint)BitConverter.ToInt64(buffer, 8);
        if (length == 0 || stringBuffer == nint.Zero || maximumLength < length || length > 4096)
            return null;

        var textBytes = new byte[length];
        return ReadProcessMemory(processHandle, stringBuffer, textBytes, textBytes.Length, out var textRead) && textRead == textBytes.Length
            ? System.Text.Encoding.Unicode.GetString(textBytes).TrimEnd('\0')
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation64
    {
        public nint Reserved1;
        public long PebBaseAddress;
        public nint Reserved2_0;
        public nint Reserved2_1;
        public nint UniqueProcessId;
        public nint Reserved3;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        ref ProcessBasicInformation64 processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        nint hProcess,
        nint lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
