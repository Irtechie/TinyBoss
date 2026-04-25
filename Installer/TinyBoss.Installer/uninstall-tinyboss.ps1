#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Uninstall TinyBoss — removes app files, shortcuts, and registry entry.
.PARAMETER Quiet
    Skip confirmation prompt.
#>
param([switch]$Quiet)

$ErrorActionPreference = "Stop"

$installDir = Join-Path $env:LOCALAPPDATA "Programs\TinyBoss"
$regPath    = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TinyBoss"
$desktop    = [Environment]::GetFolderPath("Desktop")
$startMenu  = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs"
$lnkName    = "TinyBoss.lnk"

if (-not $Quiet) {
    $answer = Read-Host "Uninstall TinyBoss? (Y/N)"
    if ($answer -notin @("Y", "y", "yes")) {
        Write-Host "Cancelled." -ForegroundColor Yellow
        exit 0
    }
}

# Kill running instance
Get-Process -Name TinyBoss -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Remove shortcuts
foreach ($dir in @($desktop, $startMenu)) {
    $lnk = Join-Path $dir $lnkName
    if (Test-Path $lnk) {
        Remove-Item $lnk -Force
        Write-Host "Removed $lnk" -ForegroundColor Gray
    }
}

# Remove registry entry
if (Test-Path $regPath) {
    Remove-Item $regPath -Force
    Write-Host "Removed registry entry" -ForegroundColor Gray
}

# Remove install directory (schedule if locked)
if (Test-Path $installDir) {
    try {
        Remove-Item $installDir -Recurse -Force
        Write-Host "Removed $installDir" -ForegroundColor Gray
    }
    catch {
        Write-Host "Could not remove install directory — it may be in use." -ForegroundColor Yellow
        Write-Host "The directory will be removed on next restart." -ForegroundColor Yellow
        # Schedule deletion on reboot
        cmd /c "rd /s /q `"$installDir`"" 2>$null
    }
}

Write-Host "`nTinyBoss has been uninstalled." -ForegroundColor Green
