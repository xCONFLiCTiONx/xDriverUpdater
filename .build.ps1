<#
.SYNOPSIS
    Build (and optionally deploy/run) xSecurityCenter.
.EXAMPLE
    .\.build.ps1                     # Release build & deploy to %LOCALAPPDATA%\xPrograms\xSecurityCenter
    .\.build.ps1 -Clean -Run         # wipe bin/obj, rebuild, deploy, launch
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Clean,
    [switch]$Run
)

Clear-Host
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$project = Join-Path $root 'src\xDriverUpdater.csproj'
$exe     = Join-Path $root "src\bin\$Configuration\net8.0-windows\xSecurityCenter.exe"

# Stop any running instance so the exe isn't locked (ignore Access Denied if elevated instance is open).
try {
    Get-Process -Name xSecurityCenter,xSystemForge,xDriverUpdater -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction Stop
    Start-Sleep -Milliseconds 400
} catch {
    Write-Host "Note: xSecurityCenter is currently running elevated. Close it before deploying." -ForegroundColor Yellow
}

if ($Clean) {
    foreach ($dir in 'bin', 'obj') {
        $path = Join-Path $root "src\$dir"
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Write-Host 'Cleaned bin/obj'
}

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath $exe)) { throw "Build succeeded but output not found: $exe" }
Write-Host "Built: $exe" -ForegroundColor Green

if ($Configuration -eq 'Release') {
    $deployDir = Join-Path $env:LOCALAPPDATA 'xPrograms\xSecurityCenter'
    $releaseBinDir = Split-Path -Parent $exe
    New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
    Copy-Item -Path "$releaseBinDir\*" -Destination $deployDir -Recurse -Force
    $exe = Join-Path $deployDir 'xSecurityCenter.exe'
    Write-Host "Deployed: $exe" -ForegroundColor Green
}

if ($Run) {
    Start-Process -FilePath $exe
    Write-Host 'Started xSecurityCenter.'
}
