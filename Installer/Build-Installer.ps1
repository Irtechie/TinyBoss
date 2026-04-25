#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build the TinyBoss installer package.
    1. Publishes the main app (self-contained win-x64)
    2. Publishes the installer app (self-contained win-x64)
    3. Assembles final layout in Installer/Output/

.EXAMPLE
    .\Installer\Build-Installer.ps1
#>

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "═══ Building TinyBoss Installer ═══" -ForegroundColor Cyan

# 1. Publish the main app
Write-Host "`n[1/3] Publishing TinyBoss app…" -ForegroundColor Yellow
$appPublish = Join-Path $PSScriptRoot "TinyBoss.Installer\app"
if (Test-Path $appPublish) { Remove-Item $appPublish -Recurse -Force }

dotnet publish "$root\TinyBoss.csproj" `
    -c Release `
    --self-contained `
    -r win-x64 `
    -o $appPublish `
    --nologo -v quiet

if ($LASTEXITCODE -ne 0) { throw "App publish failed" }

# Copy uninstall script into app bundle
Copy-Item (Join-Path $PSScriptRoot "TinyBoss.Installer\uninstall-tinyboss.ps1") `
          (Join-Path $appPublish "uninstall-tinyboss.ps1") -Force

Write-Host "  App published: $(Get-ChildItem $appPublish -Recurse -File | Measure-Object -Property Length -Sum | ForEach-Object { '{0:N0} MB' -f ($_.Sum / 1MB) })" -ForegroundColor Gray

# 2. Publish the installer
Write-Host "`n[2/3] Publishing installer…" -ForegroundColor Yellow
$installerPublish = Join-Path $PSScriptRoot "Output\staging"
if (Test-Path $installerPublish) { Remove-Item $installerPublish -Recurse -Force }

dotnet publish (Join-Path $PSScriptRoot "TinyBoss.Installer\TinyBoss.Installer.csproj") `
    -c Release `
    --self-contained `
    -r win-x64 `
    -o $installerPublish `
    --nologo -v quiet

if ($LASTEXITCODE -ne 0) { throw "Installer publish failed" }

# 3. Assemble final layout
Write-Host "`n[3/3] Assembling installer package…" -ForegroundColor Yellow
$output = Join-Path $PSScriptRoot "Output\TinyBoss-Installer"
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item $output -ItemType Directory -Force | Out-Null

# Copy installer exe + runtime dlls (everything except the app/ subfolder)
Get-ChildItem $installerPublish -File | Copy-Item -Destination $output
Get-ChildItem $installerPublish -Directory | Where-Object Name -ne "app" | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $output $_.Name) -Recurse
}

# Copy app bundle alongside installer
Copy-Item $appPublish (Join-Path $output "app") -Recurse

$totalSize = Get-ChildItem $output -Recurse -File | Measure-Object -Property Length -Sum
Write-Host "`n═══ Build Complete ═══" -ForegroundColor Green
Write-Host "Installer package: $output" -ForegroundColor White
Write-Host "Total size: $("{0:N0} MB" -f ($totalSize.Sum / 1MB))" -ForegroundColor White
Write-Host "Files: $($totalSize.Count)" -ForegroundColor White
Write-Host "`nTo install: run TinyBoss.Installer.exe from that directory." -ForegroundColor Gray
