# ============================================================================
#  Start the accounting app automatically every time you sign in to Windows.
#
#      powershell -ExecutionPolicy Bypass -File .\autostart-windows.ps1          # turn on
#      powershell -ExecutionPolicy Bypass -File .\autostart-windows.ps1 -Remove  # turn off
#
#  Creates a Task Scheduler task for the current user (no administrator
#  rights needed). It runs start-windows.ps1 hidden, 30 seconds after
#  sign-in so the network and PostgreSQL are up; output goes to
#  .local\autostart.log.
# ============================================================================
param([switch]$Remove)

$TaskName = 'Accounting app'
$Root     = $PSScriptRoot

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Automatic start turned off." -ForegroundColor Green
    exit 0
}

$start = Join-Path $Root 'start-windows.ps1'
$log   = Join-Path $Root '.local\autostart.log'
$cmd   = "& '$start' -NoBrowser *> '$log'"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -WorkingDirectory $Root `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"$cmd`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$trigger.Delay = 'PT30S'
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Description 'Starts the accounting app (start-windows.ps1) at sign-in.' -Force | Out-Null
Write-Host "Automatic start turned on: the app starts 30 seconds after you sign in to Windows." -ForegroundColor Green
Write-Host "Log: $log"
