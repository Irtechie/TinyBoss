$ErrorActionPreference = 'SilentlyContinue'

$taskName = 'TinyBoss Elevated Startup'
$installDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $installDir 'TinyBoss.exe'
$logPath = Join-Path $installDir 'restart.log'

function Write-RestartLog {
    param([string]$Message)

    try {
        Add-Content -LiteralPath $logPath -Value "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')] $Message"
    } catch {
    }
}

Write-RestartLog 'Desktop restart requested.'

try {
    & schtasks.exe /End /TN $taskName | Out-Null
    Write-RestartLog 'Requested scheduled task stop.'
} catch {
    Write-RestartLog "Scheduled task stop failed: $($_.Exception.Message)"
}

Start-Sleep -Milliseconds 750

try {
    Get-Process TinyBoss -ErrorAction SilentlyContinue | ForEach-Object {
        Write-RestartLog "Stopping stale process pid=$($_.Id)."
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
} catch {
    Write-RestartLog "Process stop failed: $($_.Exception.Message)"
}

Start-Sleep -Milliseconds 750

try {
    & schtasks.exe /Run /TN $taskName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-RestartLog 'Started elevated scheduled task.'
    } else {
        Write-RestartLog "Scheduled task start exited $LASTEXITCODE; falling back to direct launch."
        Start-Process -FilePath $exePath -WorkingDirectory $installDir
    }
} catch {
    Write-RestartLog "Scheduled task start failed: $($_.Exception.Message); falling back to direct launch."
    Start-Process -FilePath $exePath -WorkingDirectory $installDir
}

for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Milliseconds 500
    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8033/health' -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) {
            Write-RestartLog 'TinyBoss health check passed.'
            exit 0
        }
    } catch {
    }
}

Write-RestartLog 'TinyBoss health check did not pass before timeout.'
exit 1
