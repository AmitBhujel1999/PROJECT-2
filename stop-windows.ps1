# Stops the backend and frontend started by start-windows.ps1.
#   powershell -ExecutionPolicy Bypass -File .\stop-windows.ps1
param([switch]$Quiet)
$ErrorActionPreference = 'SilentlyContinue'
$pidFile = Join-Path $PSScriptRoot '.local/pids.txt'

$stopped = 0
if (Test-Path $pidFile) {
    foreach ($id in Get-Content $pidFile) {
        if ($id -match '^\d+$') {
            # /T also stops child processes (python/waitress, the Vite server)
            & taskkill.exe /PID $id /T /F *> $null
            if ($LASTEXITCODE -eq 0) { $stopped++ }
        }
    }
    Remove-Item $pidFile
}

# Anything still holding the app ports (e.g. after a crash)
foreach ($port in 8000, 4173) {
    Get-NetTCPConnection -LocalPort $port -State Listen | ForEach-Object {
        & taskkill.exe /PID $_.OwningProcess /T /F *> $null
        $stopped++
    }
}

if (-not $Quiet) {
    if ($stopped) { Write-Host "Accounting & Inventory stopped." } else { Write-Host "Nothing was running." }
    Write-Host "(PostgreSQL keeps running as a Windows service; your data is kept.)"
}
