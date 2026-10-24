# Builds installer\out\MeroLekhapalSetup.exe: web screens, the program (self-contained, so no .NET
# install is needed on the PC) and the Inno Setup installer.
# Needs: bun, .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "installer\out"

Write-Host "1/3 Web screens"
Push-Location (Join-Path $root "..\frontend")
try {
    $env:ADAPTER = "static"
    bun run build
    if ($LASTEXITCODE -ne 0) { throw "frontend build failed" }
} finally {
    Remove-Item Env:ADAPTER -ErrorAction SilentlyContinue
    Pop-Location
}

Write-Host "2/3 Program"
if (Test-Path "$out\app") { Remove-Item "$out\app" -Recurse -Force }
dotnet publish (Join-Path $root "src\Accounting.Desktop") -c Release -r win-x64 --self-contained -p:Version=$Version -o "$out\app"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "3/3 Installer"
$redist = Join-Path $root "installer\redist\MicrosoftEdgeWebview2Setup.exe"
if (-not (Test-Path $redist)) {
    New-Item -ItemType Directory -Force (Split-Path $redist) | Out-Null
    Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $redist
}
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc "/DAppVersion=$Version" (Join-Path $root "installer\MeroLekhapal.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "Done: $out\MeroLekhapalSetup.exe"
