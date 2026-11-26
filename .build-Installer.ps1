<#
.SYNOPSIS
    Builds the standalone Inno Setup 6 installer (xDriverUpdater-Setup.exe) for xDriverUpdater v1.0.0.
#>
[CmdletBinding()]
param(
    [switch]$RebuildApp,
    [switch]$Install,
    [string]$OutputDir = "",
    [string]$IsccPath  = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

Set-StrictMode -Version Latest
Clear-Host
$ErrorActionPreference = "Stop"

$repoRoot      = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = $repoRoot
}
$folderName    = "xSystemForge"
$appName       = "xSystemForge"
$installerBase = "xSystemForge-Setup"
$exeName       = "xSystemForge.exe"
$version       = "2.0.0"
$appId         = "E7C3A914-5B2D-4F8A-9C1E-6D4B8A2F7E05"
$description   = "All-in-One Windows Driver, Telemetry, Debloat and Privacy Suite"
$installDir    = Join-Path $env:LOCALAPPDATA "xPrograms\$folderName"
$sourceDir     = Join-Path $repoRoot "src\bin\Release\net8.0-windows"
$repoIcon      = Join-Path $repoRoot "src\icon.ico"

if (-not (Test-Path $IsccPath)) {
    throw "Inno Setup 6 compiler (ISCC.exe) not found at: $IsccPath"
}

if ($RebuildApp -or -not (Test-Path (Join-Path $sourceDir $exeName))) {
    $buildScript = Join-Path $repoRoot ".build.ps1"
    if (Test-Path $buildScript) {
        Write-Host "==> Running .build.ps1 for $appName..." -ForegroundColor Cyan
        & $buildScript -Configuration Release
    }
}

if (-not (Test-Path $sourceDir)) {
    throw "Release build folder not found: $sourceDir. Run .\.build.ps1 first."
}

$destIcon = Join-Path $sourceDir "icon.ico"
if ((-not (Test-Path $destIcon)) -and (Test-Path $repoIcon)) {
    Copy-Item -Path $repoIcon -Destination $destIcon -Force
}

$setupIconLine = if (Test-Path $destIcon) { "SetupIconFile=`"$destIcon`"" } else { "" }

$tempBuildDir = Join-Path $env:TEMP "xPrograms-InstallerBuild\$folderName"
$stageOutDir  = Join-Path $tempBuildDir "out"
New-Item -ItemType Directory -Path $stageOutDir -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$issLines = @(
    "; Generated Inno Setup 6 Script for $appName",
    "#define AppName        `"$appName`"",
    "#define AppFolderName  `"$folderName`"",
    "#define AppVersion     `"$version`"",
    "#define AppPublisher   `"xCONFLiCTiONx`"",
    "#define AppURL         `"https://xconflictionx.cc`"",
    "#define AppContact     `"info@xconflictionx.cc`"",
    "#define AppExeName     `"$exeName`"",
    "#define AppId          `"$appId`"",
    "#define AppDescription `"$description`"",
    "#define SourcePath     `"$sourceDir`"",
    "#define OutputPath     `"$stageOutDir`"",
    "",
    "[Setup]",
    "AppId={{{#AppId}}",
    "AppName={#AppName}",
    "AppVersion={#AppVersion}",
    "AppVerName={#AppName} {#AppVersion}",
    "AppPublisher={#AppPublisher}",
    "AppPublisherURL={#AppURL}",
    "AppSupportURL={#AppURL}",
    "AppUpdatesURL={#AppURL}",
    "AppContact={#AppContact}",
    "AppComments={#AppDescription}",
    "DefaultDirName={localappdata}\xPrograms\{#AppFolderName}",
    "DefaultGroupName=xPrograms",
    "DisableProgramGroupPage=yes",
    "PrivilegesRequired=lowest",
    "PrivilegesRequiredOverridesAllowed=dialog commandline",
    "ArchitecturesAllowed=x64compatible",
    "ArchitecturesInstallIn64BitMode=x64compatible",
    "WizardStyle=modern",
    $setupIconLine,
    "UninstallDisplayIcon={app}\{#AppExeName}",
    "UninstallDisplayName={#AppName}",
    "OutputDir={#OutputPath}",
    "OutputBaseFilename=$installerBase",
    "Compression=lzma2/fast",
    "SolidCompression=yes",
    "CloseApplications=force",
    "RestartApplications=no",
    "VersionInfoVersion={#AppVersion}",
    "VersionInfoDescription={#AppDescription}",
    "",
    "[Languages]",
    "Name: `"english`"; MessagesFile: `"compiler:Default.isl`"",
    "",
    "[Tasks]",
    "Name: `"desktopicon`"; Description: `"{cm:CreateDesktopIcon}`"; GroupDescription: `"{cm:AdditionalIcons}`"; Flags: unchecked",
    "",
    "[Files]",
    "Source: `"{#SourcePath}\*`"; DestDir: `"{app}`"; Excludes: `"unins*.*,*.pdb,Backups\*,Downloads\*,settings.json`"; Flags: ignoreversion recursesubdirs createallsubdirs",
    "",
    "[Icons]",
    "Name: `"{userprograms}\{#AppName}`"; Filename: `"{app}\{#AppExeName}`"; WorkingDir: `"{app}`"; Comment: `"{#AppDescription}`"",
    "Name: `"{userdesktop}\{#AppName}`"; Filename: `"{app}\{#AppExeName}`"; WorkingDir: `"{app}`"; Comment: `"{#AppDescription}`"; Tasks: desktopicon",
    "",
    "[Registry]",
    "Root: HKCU; Subkey: `"Software\xPrograms\{#AppFolderName}`"; ValueType: string; ValueName: `"DisplayName`"; ValueData: `"{#AppName}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\xPrograms\{#AppFolderName}`"; ValueType: string; ValueName: `"InstallDir`"; ValueData: `"{app}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\xPrograms\{#AppFolderName}`"; ValueType: string; ValueName: `"ExecutablePath`"; ValueData: `"{app}\{#AppExeName}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\xPrograms\{#AppFolderName}`"; ValueType: string; ValueName: `"Version`"; ValueData: `"{#AppVersion}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}`"; ValueType: string; ValueName: `"`"; ValueData: `"{app}\{#AppExeName}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}`"; ValueType: string; ValueName: `"Path`"; ValueData: `"{app}`"; Flags: uninsdeletekey",
    "",
    "[Run]",
    "Filename: `"{app}\{#AppExeName}`"; Description: `"{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}`"; Flags: nowait postinstall skipifsilent shellexec",
    "",
    "[UninstallRun]",
    "Filename: `"taskkill.exe`"; Parameters: `"/F /IM {#AppExeName}`"; Flags: runhidden; RunOnceId: `"Kill_{#AppFolderName}`"",
    "",
    "[UninstallDelete]",
    "Type: filesandordirs; Name: `"{app}`"",
    "",
    "[Code]",
    "function PrepareToInstall(var NeedsRestart: Boolean): String;",
    "var",
    "  ResultCode: Integer;",
    "begin",
    "  Result := '';",
    "  Exec('taskkill.exe', '/F /IM `"{#AppExeName}`"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);",
    "  Sleep(300);",
    "end;"
)

$issFile = Join-Path $tempBuildDir "$folderName.iss"
Set-Content -Path $issFile -Value ($issLines -join "`r`n") -Encoding UTF8

Write-Host "==> Compiling $installerBase.exe (v$version)..." -ForegroundColor Cyan
$compiled = $false
$stagedExe = Join-Path $stageOutDir "$installerBase.exe"
for ($attempt = 1; $attempt -le 4; $attempt++) {
    if (Test-Path $stagedExe) { Remove-Item $stagedExe -Force -ErrorAction SilentlyContinue }
    & $IsccPath /Q $issFile
    if ($LASTEXITCODE -eq 0 -and (Test-Path $stagedExe)) {
        $compiled = $true
        break
    }
    Write-Host "    [Retry $attempt] Waiting briefly for file handle release..." -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}
if (-not $compiled) {
    throw "ISCC failed to compile $issFile"
}

$finalExe = Join-Path $OutputDir "$installerBase.exe"
Copy-Item -Path $stagedExe -Destination $finalExe -Force
# Sign binary/installer with xCONFLiCTiONx code-signing certificate if available
$signCert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -match 'CN=xCONFLiCTiONx' } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if ($signCert -and $signTool) {
    $appExeToSign = Join-Path $sourceDir $exeName
    if (Test-Path $appExeToSign) {
        & $signTool.FullName sign /sha1 $signCert.Thumbprint /fd SHA256 /d "$appName" /du "https://xconflictionx.cc" /tr "http://timestamp.digicert.com" /td SHA256 $appExeToSign | Out-Null
        if ($LASTEXITCODE -ne 0) {
            & $signTool.FullName sign /sha1 $signCert.Thumbprint /fd SHA256 /d "$appName" /du "https://xconflictionx.cc" $appExeToSign | Out-Null
        }
    }
    Write-Host "==> Signing $finalExe with xCONFLiCTiONx ($($signCert.Thumbprint))..." -ForegroundColor Cyan
    & $signTool.FullName sign /sha1 $signCert.Thumbprint /fd SHA256 /d "$appName Setup" /du "https://xconflictionx.cc" /tr "http://timestamp.digicert.com" /td SHA256 $finalExe | Out-Null
    if ($LASTEXITCODE -ne 0) {
        & $signTool.FullName sign /sha1 $signCert.Thumbprint /fd SHA256 /d "$appName Setup" /du "https://xconflictionx.cc" $finalExe | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    }
    Write-Host "[OK] Signed installer: $finalExe" -ForegroundColor Green
}

Write-Host "[OK] Installer created at: $finalExe" -ForegroundColor Green

if ($Install) {
    Write-Host "==> Running silent install for $appName..." -ForegroundColor Yellow
    $procName = [System.IO.Path]::GetFileNameWithoutExtension($exeName)
    $wasRunning = Get-Process -Name $procName -ErrorAction SilentlyContinue
    if ($wasRunning) {
        $wasRunning | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
    $p = Start-Process -FilePath $finalExe -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        throw "Silent install failed with exit code $($p.ExitCode)"
    }
    Write-Host "[OK] Installed & registered $appName v$version in $installDir" -ForegroundColor Green
}
