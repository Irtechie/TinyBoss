using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TinyBoss.Installer;

public enum WizardPage { Welcome, Installing, Complete }

public partial class InstallerViewModel : ObservableObject
{
    [ObservableProperty] private WizardPage _currentPage = WizardPage.Welcome;
    [ObservableProperty] private string _overallStatus = "";
    [ObservableProperty] private string _logText = "";

    public ObservableCollection<CheckItem> Checks { get; } = new();

    public InstallerViewModel()
    {
        Checks.Add(new CheckItem
        {
            Id = "copy",
            Name = "App Files",
            Description = "Copy TinyBoss to local install directory",
            Checker = ct => AppInstaller.DetectAppFiles(ct),
            Installer = (p, ct) => AppInstaller.InstallAppFiles(p, ct),
        });
        Checks.Add(new CheckItem
        {
            Id = "shortcuts",
            Name = "Desktop Shortcuts",
            Description = "Desktop and Start Menu shortcuts",
            DependsOn = ["copy"],
            Checker = ct => AppInstaller.DetectShortcuts(ct),
            Installer = (p, ct) => AppInstaller.InstallShortcuts(p, ct),
        });
        Checks.Add(new CheckItem
        {
            Id = "registry",
            Name = "Add/Remove Programs",
            Description = "Register for Windows uninstall",
            DependsOn = ["copy"],
            Checker = ct => AppInstaller.DetectRegistration(ct),
            Installer = (p, ct) => AppInstaller.RegisterApp(p, ct),
        });
    }

    [RelayCommand]
    private void GoToInstall()
    {
        CurrentPage = WizardPage.Installing;
        _ = RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        OverallStatus = "Installing…";
        Log("=== TinyBoss Install Started ===");

        foreach (var check in Checks)
        {
            // Check dependencies
            var unmet = check.DependsOn
                .Select(id => Checks.FirstOrDefault(c => c.Id == id))
                .Where(dep => dep is null || dep.Status is not (CheckStatus.Found or CheckStatus.Installed))
                .Select(dep => dep?.Name ?? "unknown")
                .ToArray();
            if (unmet.Length > 0)
            {
                check.Status = CheckStatus.Skipped;
                check.StatusMessage = $"Waiting on: {string.Join(", ", unmet)}";
                Log($"⏭ {check.Name}: skipped — {check.StatusMessage}");
                continue;
            }

            // Detect first
            check.Status = CheckStatus.Checking;
            check.StatusMessage = "Checking…";
            try
            {
                var existing = await check.Checker!(CancellationToken.None);
                if (existing.Success)
                {
                    check.Status = CheckStatus.Found;
                    check.DetectedVersion = existing.Version;
                    check.StatusMessage = existing.Message;
                    Log($"✅ {check.Name}: already present ({existing.Version})");
                    continue;
                }
            }
            catch (Exception ex)
            {
                Log($"⚠ {check.Name} detect: {ex.Message}");
            }

            // Install
            check.Status = CheckStatus.Installing;
            check.ProgressPercent = 0;
            OverallStatus = $"Installing {check.Name}…";
            Log($"▶ Installing {check.Name}…");

            try
            {
                var result = await check.Installer!(
                    (percent, msg) => Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        check.ProgressPercent = percent;
                        check.StatusMessage = msg;
                    }),
                    CancellationToken.None);

                check.Status = result.Success ? CheckStatus.Installed : CheckStatus.Failed;
                check.DetectedVersion = result.Version;
                check.StatusMessage = result.Message;
                check.ProgressPercent = 100;
                Log(result.Success
                    ? $"✅ {check.Name}: {result.Version}"
                    : $"❌ {check.Name}: {result.Message}");
            }
            catch (Exception ex)
            {
                check.Status = CheckStatus.Failed;
                check.StatusMessage = ex.Message;
                Log($"❌ {check.Name}: {ex.Message}");
            }
        }

        var allGood = Checks.All(c => c.Status is CheckStatus.Found or CheckStatus.Installed);
        OverallStatus = allGood ? "Install complete ✅" : "Some steps failed — see details";
        Log("=== Install Complete ===\n");

        if (allGood)
            CurrentPage = WizardPage.Complete;
    }

    [RelayCommand]
    private void LaunchApp()
    {
        var exe = Path.Combine(AppInstaller.InstallDir, "TinyBoss.exe");
        if (File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = AppInstaller.InstallDir,
            });
        }
        else
        {
            OverallStatus = "Could not find KittenHerder.exe — launch manually.";
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime lt)
                lt.Shutdown(0);
        });
    }

    [RelayCommand]
    private void Close()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime lt)
                lt.Shutdown(0);
        });
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Dispatcher.UIThread.InvokeAsync(() => LogText += line + "\n");

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TinyBoss");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "install.log"), line + "\n");
        }
        catch { /* best effort */ }
    }
}
