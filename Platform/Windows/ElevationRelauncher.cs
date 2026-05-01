using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Keeps normal shortcut launches from downgrading TinyBoss to a non-elevated
/// process. Window positioning fails against elevated terminals from a limited
/// process, so installed builds should hand off to the elevated startup task.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevationRelauncher
{
    public const string StartupTaskName = "TinyBoss Elevated Startup";

    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TinyBoss",
        "elevation-relaunch.marker");

    private static readonly TimeSpan MarkerTtl = TimeSpan.FromSeconds(30);

    public static bool TryRelaunchFromInstalledTask(Action<string>? log = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        var isElevated = IsCurrentProcessElevated();
        var taskExists = ScheduledTaskExists(StartupTaskName);
        var markerRecent = IsMarkerRecent(DateTimeOffset.UtcNow);
        if (!ShouldAttemptRelaunch(true, isElevated, taskExists, markerRecent))
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not write elevation relaunch marker: {ex.Message}");
        }

        var result = RunProcess("schtasks.exe", $"/Run /TN \"{StartupTaskName}\"");
        if (result.ExitCode == 0)
        {
            log?.Invoke("Started elevated TinyBoss scheduled task; exiting limited launcher.");
            return true;
        }

        log?.Invoke(
            $"Could not start elevated scheduled task ({result.ExitCode}); continuing limited. {result.Error}{result.Output}");
        return false;
    }

    public static bool ShouldAttemptRelaunch(
        bool isWindows,
        bool isElevated,
        bool taskExists,
        bool recentRelaunchMarker) =>
        isWindows && !isElevated && taskExists && !recentRelaunchMarker;

    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool ScheduledTaskExists(string taskName)
    {
        var result = RunProcess("schtasks.exe", $"/Query /TN \"{taskName}\"");
        return result.ExitCode == 0;
    }

    private static bool IsMarkerRecent(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(MarkerPath))
                return false;

            var text = File.ReadAllText(MarkerPath).Trim();
            return DateTimeOffset.TryParse(text, out var markedAt)
                && now - markedAt < MarkerTtl;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessResult RunProcess(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
                return new ProcessResult(-1, "", "Process.Start returned null");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return new ProcessResult(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
