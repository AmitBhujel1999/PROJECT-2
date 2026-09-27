# One-command start for Windows PowerShell:  .\start.ps1
# (If blocked: powershell -ExecutionPolicy Bypass -File .\start.ps1)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker is not installed or not running. Install/start Docker Desktop first:"
    Write-Host "  https://www.docker.com/products/docker-desktop"
    exit 1
}

function Rand([int]$n) {
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $bytes = New-Object byte[] $n
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

if (-not (Test-Path .env)) {
    $dbPass = Rand 24
    $adminPass = "Admin-" + (Rand 10)
    $content = Get-Content .env.example -Raw
    $content = $content -replace '(?m)^SECRET_KEY=.*', ("SECRET_KEY=" + (Rand 64))
    $content = $content -replace 'CHANGE-ME-db-password', $dbPass
    $content = $content -replace '(?m)^DJANGO_SUPERUSER_PASSWORD=.*', "DJANGO_SUPERUSER_PASSWORD=$adminPass"
    $content = $content -replace '(?m)^LOAD_DEMO_DATA=.*', 'LOAD_DEMO_DATA=True'
    # Write without BOM, LF endings
    [System.IO.File]::WriteAllText("$PSScriptRoot\.env", ($content -replace "`r`n", "`n"))
    Write-Host "Created .env with random secrets."
}

docker compose up -d --build
if ($LASTEXITCODE -ne 0) { exit 1 }

$port = ((Get-Content .env | Select-String '^HTTP_PORT=') -replace 'HTTP_PORT=', '').Trim()
$url = if ($port -and $port -ne '80') { "http://localhost:$port" } else { 'http://localhost' }
Write-Host "Waiting for the app to become ready..."
$ready = $false
for ($i = 0; $i -lt 120; $i++) {
    try { Invoke-WebRequest "$url/api/health/" -UseBasicParsing -TimeoutSec 3 | Out-Null; $ready = $true; break } catch { Start-Sleep 2 }
}
if (-not $ready) { Write-Host "Not ready after 4 minutes; check: docker compose logs backend"; exit 1 }

$user = ((Get-Content .env | Select-String '^DJANGO_SUPERUSER_USERNAME=') -replace 'DJANGO_SUPERUSER_USERNAME=', '').Trim()
$pass = ((Get-Content .env | Select-String '^DJANGO_SUPERUSER_PASSWORD=') -replace 'DJANGO_SUPERUSER_PASSWORD=', '').Trim()
Write-Host ""
Write-Host "============================================================"
Write-Host " Accounting & Inventory is running:  $url"
Write-Host " Username: $user"
Write-Host " Password: $pass"
Write-Host " (stored in .env - change it after first login via Profile)"
Write-Host " Stop with: docker compose down"
Write-Host "============================================================"
Start-Process $url
