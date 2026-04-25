using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TinyBoss.Installer;

public enum CheckStatus
{
    NotChecked,
    Checking,
    Found,
    Missing,
    Installing,
    Installed,
    Failed,
    Skipped,
}

public partial class CheckItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string FailureHint { get; init; } = "";
    public string[] DependsOn { get; init; } = [];

    [ObservableProperty] private CheckStatus _status = CheckStatus.NotChecked;
    [ObservableProperty] private string _detectedVersion = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private int _progressPercent;

    public Func<CancellationToken, Task<CheckResult>>? Checker { get; init; }
    public Func<Action<int, string>, CancellationToken, Task<CheckResult>>? Installer { get; init; }

    public bool NeedsInstall => Status is CheckStatus.Missing or CheckStatus.Failed;
    public bool CanInstall => NeedsInstall && IsSelected && Installer is not null;
}

public record CheckResult(bool Success, string Version = "", string Message = "");
