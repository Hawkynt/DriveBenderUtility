<#
.SYNOPSIS
  Remove what install.ps1 installed: the published DriveBender directory and its
  entry on the user PATH. The Windows counterpart of uninstall.sh.

.DESCRIPTION
  Does NOT touch your pools, their data, or your config under
  %ProgramData%\DriveBenderUtility — removing the tool never removes a storage
  array. Use -PurgeConfig to also delete the config/registry (never pool data).

.PARAMETER Prefix
  Install root used at install time. Default: %LOCALAPPDATA%\Programs\DriveBender.

.PARAMETER PurgeConfig
  Also delete %ProgramData%\DriveBenderUtility (config + mount registry).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
[CmdletBinding()]
param(
  [string]$Prefix = (Join-Path $env:LOCALAPPDATA 'Programs\DriveBender'),
  [switch]$PurgeConfig
)

$ErrorActionPreference = 'Stop'
function Say  ($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn ($m) { Write-Host "!!  $m" -ForegroundColor Yellow }

$LibDir = Join-Path $Prefix 'lib'

# drop the lib dir from the user PATH (install.ps1 added it there)
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath) {
  $kept = ($userPath -split ';') | Where-Object { $_ -and $_ -ne $LibDir }
  $newPath = ($kept -join ';')
  if ($newPath -ne $userPath) {
    Say "Removing $LibDir from your user PATH"
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    Warn "Open a new terminal for the PATH change to take effect."
  }
}

if (Test-Path $LibDir) {
  Say "Removing $LibDir"
  Remove-Item -Recurse -Force $LibDir
} else {
  Warn "$LibDir not found — nothing to remove there"
}
# clean up an empty install root left behind
if ((Test-Path $Prefix) -and -not (Get-ChildItem -Force $Prefix)) { Remove-Item -Force $Prefix }

if ($PurgeConfig) {
  $config = Join-Path $env:ProgramData 'DriveBenderUtility'
  if (Test-Path $config) {
    Say "Purging config/registry $config (pool DATA on your drives is untouched)"
    Remove-Item -Recurse -Force $config
  }
}

Say "Uninstalled. Any pool still mounted keeps serving until you unmount it."
