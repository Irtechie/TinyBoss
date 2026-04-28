using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TinyBoss.Installer;

/// <summary>
/// Handles file copy, shortcut creation, and Add/Remove Programs registration.
/// Per-user install (no admin required).
/// </summary>
public static class AppInstaller
{
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "TinyBoss");

    private const string UninstallRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TinyBoss";

    private const string AppExeName = "TinyBoss.exe";
    private const string ShortcutName = "TinyBoss.lnk";
    private const string StartupTaskName = "TinyBoss Elevated Startup";

    // ── Detection ────────────────────────────────────────────────────────

    public static Task<CheckResult> DetectAppFiles(CancellationToken ct)
    {
        var exe = Path.Combine(InstallDir, AppExeName);
        if (File.Exists(exe))
        {
            var ver = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "installed";
            return Task.FromResult(new CheckResult(true, ver));
        }
        return Task.FromResult(new CheckResult(false, "", "Not installed"));
    }

    public static Task<CheckResult> DetectShortcuts(CancellationToken ct)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");

        var hasDesktop = File.Exists(Path.Combine(desktop, ShortcutName));
        var hasStart = File.Exists(Path.Combine(startMenu, ShortcutName));

        if (hasDesktop && hasStart)
            return Task.FromResult(new CheckResult(true, "Desktop + Start Menu"));
        return Task.FromResult(new CheckResult(false, "",
            hasDesktop ? "Missing Start Menu shortcut" : hasStart ? "Missing Desktop shortcut" : "No shortcuts"));
    }

    public static Task<CheckResult> DetectRegistration(CancellationToken ct)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath);
        if (key is not null)
            return Task.FromResult(new CheckResult(true, "Registered"));
        return Task.FromResult(new CheckResult(false, "", "Not registered in Add/Remove Programs"));
    }

    public static async Task<CheckResult> DetectElevatedStartup(CancellationToken ct)
    {
        var result = await RunProcess(
            "schtasks.exe",
            $"/Query /TN \"{StartupTaskName}\" /FO LIST",
            ct);

        return result.ExitCode == 0
            ? new CheckResult(true, "Scheduled task", "Runs elevated at Windows logon")
            : new CheckResult(false, "", "Elevated startup task not registered");
    }

    // ── Installation ─────────────────────────────────────────────────────

    public static async Task<CheckResult> InstallAppFiles(
        Action<int, string> progress, CancellationToken ct)
    {
        progress(10, "Locating app bundle…");

        var bundledDir = FindBundledApp();
        if (bundledDir is null)
            return new(false, "", "App bundle not found — re-download the installer");

        progress(30, "Copying files…");
        Directory.CreateDirectory(InstallDir);
        CopyDirectory(bundledDir, InstallDir, overwrite: true);
        CleanupLegacyKittenHerderFiles();

        // Copy uninstall script
        var uninstallSrc = Path.Combine(bundledDir, "uninstall-tinyboss.ps1");
        if (File.Exists(uninstallSrc))
            File.Copy(uninstallSrc, Path.Combine(InstallDir, "uninstall-tinyboss.ps1"), overwrite: true);

        progress(90, "Verifying…");
        var exe = Path.Combine(InstallDir, AppExeName);
        if (!File.Exists(exe))
            return new(false, "", $"{AppExeName} not found after copy");

        var ver = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "1.0.0";
        progress(100, "Files installed ✅");
        return new(true, ver);
    }

    public static async Task<CheckResult> InstallShortcuts(
        Action<int, string> progress, CancellationToken ct)
    {
        progress(20, "Creating shortcuts…");

        var exePath = Path.Combine(InstallDir, AppExeName);
        var iconPath = Path.Combine(InstallDir, "Assets", "TinyBoss.ico");
        var shortcutIcon = File.Exists(iconPath) ? iconPath : exePath;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");

        foreach (var dir in new[] { desktop, startMenu })
        {
            var lnkPath = Path.Combine(dir, ShortcutName);
            var ps = $"""
                $ws = New-Object -ComObject WScript.Shell
                $sc = $ws.CreateShortcut('{lnkPath}')
                $sc.TargetPath = '{exePath}'
                $sc.WorkingDirectory = '{InstallDir}'
                $sc.IconLocation = '{shortcutIcon}'
                $sc.Description = 'TinyBoss - Window Manager & Voice CLI'
                $sc.Save()
                """;
            await RunPowerShell(ps, ct);
        }

        progress(90, "Verifying…");
        var result = await DetectShortcuts(ct);
        progress(100, result.Success ? "Shortcuts created ✅" : result.Message);
        return result;
    }

    public static Task<CheckResult> RegisterApp(
        Action<int, string> progress, CancellationToken ct)
    {
        progress(50, "Registering in Add/Remove Programs…");

        var exePath = Path.Combine(InstallDir, AppExeName);
        var iconPath = Path.Combine(InstallDir, "Assets", "TinyBoss.ico");
        var displayIcon = File.Exists(iconPath) ? iconPath : exePath;
        var uninstallScript = Path.Combine(InstallDir, "uninstall-tinyboss.ps1");
        var uninstallCommand = File.Exists(uninstallScript)
            ? $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{uninstallScript}\""
            : $"cmd.exe /c rd /s /q \"{InstallDir}\"";

        var version = File.Exists(exePath)
            ? FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "1.0.0"
            : "1.0.0";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath);
            if (key is null)
                return Task.FromResult(new CheckResult(false, "", "Could not create registry key"));

            key.SetValue("DisplayName", "TinyBoss");
            key.SetValue("Publisher", "Irtechie");
            key.SetValue("DisplayIcon", displayIcon);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", uninstallCommand);
            key.SetValue("QuietUninstallString", $"{uninstallCommand} -Quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("DisplayVersion", version);

            // Estimate installed size in KB
            try
            {
                var size = new DirectoryInfo(InstallDir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length) / 1024;
                key.SetValue("EstimatedSize", (int)size, RegistryValueKind.DWord);
            }
            catch { /* best effort */ }

            progress(100, "Registered ✅");
            return Task.FromResult(new CheckResult(true, "Registered"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckResult(false, "", ex.Message));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string? FindBundledApp()
    {
        // Look for "app/" next to the running installer exe
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "app");
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, AppExeName)))
            return candidate;

        // Dev: look in parent dir
        var parent = Path.GetDirectoryName(baseDir);
        if (parent is not null)
        {
            candidate = Path.Combine(parent, "app");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, AppExeName)))
                return candidate;
        }

        return null;
    }

    private static void CopyDirectory(string src, string dst, bool overwrite)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var target = Path.Combine(dst, Path.GetFileName(file));
            File.Copy(file, target, overwrite);
        }
        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            var target = Path.Combine(dst, Path.GetFileName(dir));
            CopyDirectory(dir, target, overwrite);
        }
    }

    public static async Task<CheckResult> RegisterElevatedStartup(
        Action<int, string> progress, CancellationToken ct)
    {
        progress(20, "Creating elevated startup task…");

        var exePath = Path.Combine(InstallDir, AppExeName);
        if (!File.Exists(exePath))
            return new(false, "", $"{AppExeName} not installed yet");

        var script = $"""
            $ErrorActionPreference = 'Stop'
            $taskName = '{StartupTaskName}'
            $exePath = '{exePath}'
            $workDir = '{InstallDir}'
            $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            $action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $workDir
            $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -StartWhenAvailable
            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Start TinyBoss elevated at user logon so it can manage elevated terminal windows.' -Force | Out-Null
            """;

        var result = await RunPowerShellResult(script, ct);
        if (result.ExitCode != 0)
            return new(false, "", string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

        progress(90, "Verifying…");
        var detect = await DetectElevatedStartup(ct);
        progress(100, detect.Success ? "Elevated startup registered ✅" : detect.Message);
        return detect.Success ? new(true, detect.Version, detect.Message) : detect;
    }

    private static void CleanupLegacyKittenHerderFiles()
    {
        string[] legacyFiles =
        [
            "KittenHerder.exe",
            "KittenHerder.dll",
            "KittenHerder.pdb",
            "KittenHerder.deps.json",
            "KittenHerder.runtimeconfig.json",
            "web.config",
        ];

        foreach (var fileName in legacyFiles)
        {
            var path = Path.Combine(InstallDir, fileName);
            if (!File.Exists(path))
                continue;

            try { File.Delete(path); }
            catch { /* Stale cleanup is best effort; install verification catches real failures. */ }
        }
    }

    private static async Task RunPowerShell(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc is not null)
            await proc.WaitForExitAsync(ct);
    }

    private static Task<ProcessResult> RunPowerShellResult(string script, CancellationToken ct) =>
        RunProcess("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"", ct);

    private static async Task<ProcessResult> RunProcess(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return new(-1, "", $"Failed to start {fileName}");

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new(proc.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
