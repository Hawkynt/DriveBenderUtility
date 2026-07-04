<#
.SYNOPSIS
  Build DriveBender and put 'dbmount' (the pool daemon/CLI) and 'DriveBender.App'
  (the desktop shell) on your PATH. The Windows counterpart of install.sh.

.DESCRIPTION
  Framework-dependent build: needs the .NET 10 SDK to build and the .NET 10
  runtime to run (the SDK includes it). The desktop shell uses the WebView2
  runtime, which ships with Windows 11 and current Edge; this script warns if
  it is absent rather than forcing an install. Re-run any time to upgrade.

.PARAMETER Prefix
  Install root. Default: %LOCALAPPDATA%\Programs\DriveBender.

.PARAMETER Configuration
  Build configuration. Default: Release.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
  [string]$Prefix = (Join-Path $env:LOCALAPPDATA 'Programs\DriveBender'),
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

function Say  ($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn ($m) { Write-Host "!!  $m" -ForegroundColor Yellow }

# The desktop shell and dbmount share one directory so the shell can locate
# dbmount.exe as a sibling (DriveBender.App/Program.cs::_LocateDbmount).
$LibDir = Join-Path $Prefix 'lib'

# ---- 0. toolchain ---------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "The .NET SDK ('dotnet') is not on your PATH. Install .NET 10: https://dotnet.microsoft.com/download"
}
Say "Using .NET SDK $(dotnet --version)"

# ---- 1. runtime dependency: WebView2 --------------------------------------
$webview2 = @(
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
  'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
  'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($webview2) {
  Say "WebView2 runtime present"
} else {
  Warn "The WebView2 runtime was not detected. The 'dbmount' CLI still works;"
  Warn "for the desktop window install it from https://developer.microsoft.com/microsoft-edge/webview2/"
}

# ---- 2. build & publish ----------------------------------------------------
$MountTfm = 'net10.0-windows'   # Windows flavour of dbmount (WinFsp/Dokan)
$AppTfm   = 'net10.0'

Say "Restoring packages"
dotnet restore DriveBender.sln --nologo

Say "Publishing dbmount and the desktop shell into $LibDir"
if (Test-Path $LibDir) { Remove-Item -Recurse -Force $LibDir }
New-Item -ItemType Directory -Force -Path $LibDir | Out-Null
dotnet publish DriveBender.Mount\DriveBender.Mount.csproj -c $Configuration -f $MountTfm -o $LibDir --nologo
dotnet publish DriveBender.App\DriveBender.App.csproj     -c $Configuration -f $AppTfm   -o $LibDir --nologo

if (-not (Test-Path (Join-Path $LibDir 'dbmount.exe')))        { throw "publish did not produce dbmount.exe" }
if (-not (Test-Path (Join-Path $LibDir 'DriveBender.App.exe'))) { throw "publish did not produce DriveBender.App.exe" }

# ---- 3. expose on PATH -----------------------------------------------------
# Windows has no convenient symlink; add the lib dir to the user PATH and drop a
# short 'drivebender' shim next to the exes so both names are launchable.
$shim = Join-Path $LibDir 'drivebender.cmd'
Set-Content -Path $shim -Encoding ASCII -Value '@echo off
"%~dp0DriveBender.App.exe" %*'

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $LibDir) {
  Say "Adding $LibDir to your user PATH"
  [Environment]::SetEnvironmentVariable('Path', "$userPath;$LibDir", 'User')
  Warn "Open a new terminal for the PATH change to take effect."
} else {
  Say "$LibDir already on your user PATH"
}

# ---- 4. done ---------------------------------------------------------------
Say "Installed:"
Write-Host "    dbmount        -> the pool daemon/CLI  (run 'dbmount serve')"
Write-Host "    drivebender    -> the desktop manager window"
