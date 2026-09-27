# ============================================================================
#  Accounting & Inventory - run on Windows WITHOUT Docker
#
#  Usage (in PowerShell, from this folder):
#      powershell -ExecutionPolicy Bypass -File .\start-windows.ps1
#
#  First run: installs PostgreSQL 16, uv (Python) and Bun with winget,
#  creates the database, installs dependencies, builds the frontend, loads
#  demo data and starts the app. Later runs just start it again.
#  Stop it with:  powershell -ExecutionPolicy Bypass -File .\stop-windows.ps1
# ============================================================================
# Native tools (uv, bun, winget, psql) report progress on stderr; with 'Stop'
# Windows PowerShell 5.1 could treat that as fatal, so rely on exit codes.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
Set-Location $PSScriptRoot

$Root       = $PSScriptRoot
$LocalDir   = Join-Path $Root '.local'          # git-ignored: logs, pids, generated secrets
$SecretFile = Join-Path $LocalDir 'setup-secrets.txt'
$BackendEnv = Join-Path $Root 'backend/.env'
$AppPort    = 4173
$ApiPort    = 8000
$AppUrl     = "http://localhost:$AppPort"
New-Item -ItemType Directory -Force -Path $LocalDir | Out-Null

function Say($msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

function Refresh-Path {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user    = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user;$env:USERPROFILE\.local\bin;$env:USERPROFILE\.bun\bin;$env:USERPROFILE\.cargo\bin"
}

function New-Secret([int]$n) {
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $bytes = New-Object byte[] $n
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

function Read-Secrets {
    $s = @{}
    if (Test-Path $SecretFile) {
        foreach ($line in Get-Content $SecretFile) {
            if ($line -match '^(\w+)=(.*)$') { $s[$Matches[1]] = $Matches[2] }
        }
    }
    return $s
}

function Save-Secrets($s) {
    ($s.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) | Set-Content -Encoding ASCII $SecretFile
}

function Find-Psql {
    $candidates = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Directory.Parent.Name -replace '\D', '') } -Descending
    if ($candidates) { return $candidates[0].FullName }
    $cmd = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Winget-Install($id, $extra = @()) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Fail "winget is not available. Install 'App Installer' from the Microsoft Store, or install $id manually, then run this script again."
    }
    Say "Installing $id (this can take a few minutes)..."
    & winget install --id $id --exact --silent --accept-source-agreements --accept-package-agreements @extra
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189) {  # -1978335189 = already installed
        Fail "winget could not install $id (exit code $LASTEXITCODE)."
    }
    Refresh-Path
}

Refresh-Path
$secrets = Read-Secrets

# ---------------------------------------------------------------------------
# 1. Prerequisites
# ---------------------------------------------------------------------------
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { Winget-Install 'astral-sh.uv' }
if (-not (Get-Command bun -ErrorAction SilentlyContinue)) { Winget-Install 'Oven-sh.Bun' }
if (-not (Get-Command uv -ErrorAction SilentlyContinue))  { Fail "uv is still not on PATH. Close this window, open a new PowerShell and run the script again." }
if (-not (Get-Command bun -ErrorAction SilentlyContinue)) { Fail "bun is still not on PATH. Close this window, open a new PowerShell and run the script again." }

$psql = Find-Psql
if (-not $psql) {
    $secrets['PG_SUPERUSER_PASSWORD'] = New-Secret 20
    Save-Secrets $secrets
    Winget-Install 'PostgreSQL.PostgreSQL.16' @(
        '--override',
        "--mode unattended --unattendedmodeui none --superpassword $($secrets['PG_SUPERUSER_PASSWORD']) --serverport 5432 --disable-components stackbuilder"
    )
    $psql = Find-Psql
    if (-not $psql) { Fail "PostgreSQL was installed but psql.exe was not found. Restart the computer and run the script again." }
}
Say "Using PostgreSQL at $psql"

# Make sure the PostgreSQL Windows service is running
$pgService = Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pgService -and $pgService.Status -ne 'Running') {
    Say "Starting PostgreSQL service $($pgService.Name)..."
    try { Start-Service $pgService.Name -ErrorAction Stop } catch { Fail "Could not start $($pgService.Name). Run PowerShell as Administrator once, or start it from services.msc." }
}

# ---------------------------------------------------------------------------
# 2. Database and backend configuration (first run only)
# ---------------------------------------------------------------------------
if (-not (Test-Path $BackendEnv)) {
    if (-not $secrets['PG_SUPERUSER_PASSWORD']) {
        $secure = Read-Host "Enter the password of the PostgreSQL 'postgres' user (set when PostgreSQL was installed)" -AsSecureString
        $secrets['PG_SUPERUSER_PASSWORD'] = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }
    $dbPass    = New-Secret 24
    $adminPass = 'Admin-' + (New-Secret 10)

    Say "Creating database 'accounting'..."
    $env:PGPASSWORD = $secrets['PG_SUPERUSER_PASSWORD']
    $exists = & $psql -h 127.0.0.1 -U postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname='accounting'"
    if ($LASTEXITCODE -ne 0) { Remove-Item Env:PGPASSWORD; Fail "Could not connect to PostgreSQL as 'postgres'. Check the password and that the service is running." }
    if ($exists -match '1') {
        & $psql -h 127.0.0.1 -U postgres -qc "ALTER ROLE accounting WITH LOGIN PASSWORD '$dbPass' CREATEDB" | Out-Null
    } else {
        & $psql -h 127.0.0.1 -U postgres -qc "CREATE ROLE accounting WITH LOGIN PASSWORD '$dbPass' CREATEDB" | Out-Null
    }
    $dbExists = & $psql -h 127.0.0.1 -U postgres -tAc "SELECT 1 FROM pg_database WHERE datname='accounting'"
    if (-not ($dbExists -match '1')) {
        & $psql -h 127.0.0.1 -U postgres -qc "CREATE DATABASE accounting OWNER accounting" | Out-Null
    }
    Remove-Item Env:PGPASSWORD

    $envText = @"
# Generated by start-windows.ps1 - local Windows installation (no Docker)
DEBUG=False
SECRET_KEY=$(New-Secret 64)
DATABASE_URL=postgresql://accounting:$dbPass@127.0.0.1:5432/accounting
ALLOWED_HOSTS=localhost,127.0.0.1
CSRF_TRUSTED_ORIGINS=http://localhost:$AppPort,http://127.0.0.1:$AppPort
CORS_ALLOWED_ORIGINS=http://localhost:$AppPort
FRONTEND_URL=$AppUrl
USE_HTTPS=False
DJANGO_SUPERUSER_USERNAME=admin
DJANGO_SUPERUSER_EMAIL=admin@example.com
DJANGO_SUPERUSER_PASSWORD=$adminPass
EMAIL_BACKEND=django.core.mail.backends.console.EmailBackend
"@
    [System.IO.File]::WriteAllText($BackendEnv, $envText)
    $secrets['ADMIN_PASSWORD'] = $adminPass
    Save-Secrets $secrets
    $firstRun = $true
} else {
    $firstRun = $false
}

# ---------------------------------------------------------------------------
# 3. Backend: dependencies, migrations, admin, demo data, static files
# ---------------------------------------------------------------------------
Say "Installing backend dependencies (uv sync)..."
Push-Location (Join-Path $Root 'backend')
& uv sync --frozen --no-dev
if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "uv sync failed." }
& uv run --no-dev python manage.py migrate --noinput
if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "Database migration failed. See the messages above." }
& uv run --no-dev python manage.py ensure_admin
if ($firstRun) {
    Say "Loading demo data (first run only)..."
    & uv run --no-dev python manage.py seed_demo_data --if-empty
}
& uv run --no-dev python manage.py collectstatic --noinput -v 0
Pop-Location

# ---------------------------------------------------------------------------
# 4. Frontend: dependencies and production build
# ---------------------------------------------------------------------------
Push-Location (Join-Path $Root 'frontend')
Say "Installing frontend dependencies (bun install)..."
& bun install --frozen-lockfile
if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "bun install failed." }
Say "Building the frontend..."
& bun run build
if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "Frontend build failed." }
Pop-Location

# ---------------------------------------------------------------------------
# 5. Start backend (waitress) and frontend (preview server)
# ---------------------------------------------------------------------------
& (Join-Path $Root 'stop-windows.ps1') -Quiet

function Test-PortInUse([int]$port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $port); return $true } catch { return $false } finally { $client.Close() }
}
foreach ($port in $ApiPort, $AppPort) {
    if (Test-PortInUse $port) {
        Fail "Port $port is already used by another program. Close that program (or restart the computer) and run this script again."
    }
}

Say "Starting the backend on 127.0.0.1:$ApiPort..."
$backend = Start-Process -FilePath 'uv' -WorkingDirectory (Join-Path $Root 'backend') `
    -ArgumentList @('run', '--no-dev', 'waitress-serve', "--listen=127.0.0.1:$ApiPort", '--threads=8', 'config.wsgi:application') `
    -RedirectStandardOutput (Join-Path $LocalDir 'backend.log') -RedirectStandardError (Join-Path $LocalDir 'backend-error.log') `
    -WindowStyle Hidden -PassThru

Say "Starting the web app on $AppUrl..."
$frontend = Start-Process -FilePath 'bun' -WorkingDirectory (Join-Path $Root 'frontend') `
    -ArgumentList @('run', 'preview', '--host', '127.0.0.1', '--port', "$AppPort", '--strictPort') `
    -RedirectStandardOutput (Join-Path $LocalDir 'frontend.log') -RedirectStandardError (Join-Path $LocalDir 'frontend-error.log') `
    -WindowStyle Hidden -PassThru

"$($backend.Id)`n$($frontend.Id)" | Set-Content -Encoding ASCII (Join-Path $LocalDir 'pids.txt')

Say "Waiting for the app to become ready..."
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    try {
        $r = Invoke-WebRequest "$AppUrl/api/health/" -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { $ready = $true; break }
    } catch { Start-Sleep -Seconds 2 }
}
if ($ready -and ($backend.HasExited -or $frontend.HasExited)) { $ready = $false }
if (-not $ready) {
    Write-Host ""
    Write-Host "The app did not start. Last log lines:" -ForegroundColor Yellow
    Get-Content (Join-Path $LocalDir 'backend-error.log') -Tail 15 -ErrorAction SilentlyContinue
    Get-Content (Join-Path $LocalDir 'frontend-error.log') -Tail 15 -ErrorAction SilentlyContinue
    Fail "Startup failed. Full logs are in the .local folder."
}

$adminPassword = (Read-Secrets)['ADMIN_PASSWORD']
Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host " Accounting & Inventory is running:  $AppUrl" -ForegroundColor Green
Write-Host " Username: admin"
if ($adminPassword) { Write-Host " Password: $adminPassword   (also saved in .local\setup-secrets.txt)" }
Write-Host " Demo users: demo_manager / demo_accountant / demo_staff  (password Demo@12345)"
Write-Host " Stop it:  powershell -ExecutionPolicy Bypass -File .\stop-windows.ps1"
Write-Host " Logs:     .local\backend*.log, .local\frontend*.log"
Write-Host "============================================================" -ForegroundColor Green
Start-Process $AppUrl
