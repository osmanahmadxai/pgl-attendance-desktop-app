<#
.SYNOPSIS
  Build the native PGL Attendance installer (.exe) on Windows.

.DESCRIPTION
  Pure .NET pipeline — no Node.js, no Next.js, no NSSM.

  Stages:
    1. dotnet publish PglAttendance.Service  (Kestrel + sync engine)
    2. dotnet publish PglAttendance.Desktop  (WinForms UI)
    3. Pre-bake the seed SQLite DB (empty, schema applied)
    4. Stage all files into windows-build\dist\staging
    5. ISCC.exe compiles installer.iss to dist\PGL-Attendance-Setup-<ver>.exe

.PARAMETER Version
  Installer version (default 1.0.0).

.EXAMPLE
  pwsh -ExecutionPolicy Bypass -File .\windows-build\scripts\build.ps1
  .\windows-build\scripts\build.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
  [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

function Log([string]$m) { Write-Host ("[build] {0}" -f $m) -ForegroundColor Cyan }
function Die([string]$m) { Write-Host ("[build] ERROR: {0}" -f $m) -ForegroundColor Red; exit 1 }

# --- Paths -------------------------------------------------------------------
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir  = Resolve-Path (Join-Path $ScriptDir '..')
$NativeDir   = Join-Path $ProjectDir 'native'
$SolutionFile= Join-Path $NativeDir 'PglAttendance.sln'
$ServiceProj = Join-Path $NativeDir 'PglAttendance.Service\PglAttendance.Service.csproj'
$DesktopProj = Join-Path $NativeDir 'PglAttendance.Desktop\PglAttendance.Desktop.csproj'
$IssFile     = Join-Path $ProjectDir 'installer\installer.iss'
$DistDir     = Join-Path $ProjectDir 'dist'
$Staging     = Join-Path $DistDir 'staging'

# --- Prereqs -----------------------------------------------------------------
function Require-Cmd($name, $hint) {
  if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { Die "missing prerequisite: $name. $hint" }
}
Require-Cmd 'dotnet' 'Install .NET 8 SDK from https://dotnet.microsoft.com/download'

$ISCC = $null
foreach ($p in @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)) { if (Test-Path $p) { $ISCC = $p; break } }
if (-not $ISCC) { Die 'Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php' }
Log "Using ISCC: $ISCC"

# --- Fresh dirs --------------------------------------------------------------
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Staging      | Out-Null
New-Item -ItemType Directory -Force -Path "$Staging\seed" | Out-Null

# --- 1. dotnet restore -------------------------------------------------------
Log 'Restoring .NET solution...'
& dotnet restore $SolutionFile
if ($LASTEXITCODE -ne 0) { Die 'dotnet restore failed' }

# --- 2. Publish service ------------------------------------------------------
Log 'Publishing service (self-contained, single-file, win-x64)...'
$SvcOut = Join-Path $NativeDir 'PglAttendance.Service\publish'
if (Test-Path $SvcOut) { Remove-Item $SvcOut -Recurse -Force }
& dotnet publish $ServiceProj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:Version=$Version `
  -o $SvcOut
if ($LASTEXITCODE -ne 0) { Die 'service publish failed' }
$SvcExe = Join-Path $SvcOut 'PglAttendanceService.exe'
if (-not (Test-Path $SvcExe)) { Die "$SvcExe not found after publish" }
Copy-Item $SvcExe (Join-Path $Staging 'PglAttendanceService.exe')

# --- 3. Publish desktop ------------------------------------------------------
Log 'Publishing desktop UI (self-contained, single-file, win-x64)...'
$UiOut = Join-Path $NativeDir 'PglAttendance.Desktop\publish'
if (Test-Path $UiOut) { Remove-Item $UiOut -Recurse -Force }
& dotnet publish $DesktopProj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:Version=$Version `
  -o $UiOut
if ($LASTEXITCODE -ne 0) { Die 'desktop publish failed' }
$UiExe = Join-Path $UiOut 'PglAttendance.exe'
if (-not (Test-Path $UiExe)) { Die "$UiExe not found after publish" }
Copy-Item $UiExe (Join-Path $Staging 'PglAttendance.exe')

# --- 4. App icon + seed files ------------------------------------------------
Copy-Item (Join-Path $ProjectDir 'assets\app.ico') (Join-Path $Staging 'app.ico')
@{ hrmisUrl = 'https://people-api.pglsystem.com'; port = 4001 } |
  ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $Staging 'seed\settings.json')

Log 'Creating empty seed SQLite database with the schema applied...'
$SeedDb = Join-Path $Staging 'seed\attendance.db'
if (Test-Path $SeedDb) { Remove-Item $SeedDb -Force }
# Use the published service exe to bootstrap the DB (PGL_DATA_DIR override).
$tempDataDir = Join-Path ([IO.Path]::GetTempPath()) ("pgl-seed-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDataDir | Out-Null
try
{
  $env:PGL_DATA_DIR = $tempDataDir
  $proc = Start-Process -FilePath $SvcExe -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 3
  Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  $producedDb = Join-Path $tempDataDir 'attendance.db'
  if (-not (Test-Path $producedDb)) { Die 'seed DB was not created by the bootstrap run' }
  Copy-Item $producedDb $SeedDb
}
finally
{
  Remove-Item Env:PGL_DATA_DIR -ErrorAction SilentlyContinue
  Remove-Item $tempDataDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 5. ISCC compile ---------------------------------------------------------
Log 'Compiling installer with Inno Setup...'
& "$ISCC" /Q `
  "/DStagingDir=$Staging" `
  "/DMyAppVersion=$Version" `
  "/O$DistDir" `
  "$IssFile"
if ($LASTEXITCODE -ne 0) { Die "ISCC failed (exit $LASTEXITCODE)" }

$OutExe = Join-Path $DistDir "PGL-Attendance-Setup-$Version.exe"
if (-not (Test-Path $OutExe)) { Die "$OutExe missing" }

$Mb = [math]::Round((Get-Item $OutExe).Length / 1MB, 1)
Log "Done. Installer: $OutExe ($Mb MB)"
