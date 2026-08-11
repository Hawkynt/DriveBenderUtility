<#
.SYNOPSIS
  Turns failed tests in a .trx file into GitHub Actions error annotations.

.DESCRIPTION
  Job LOGS and ARTIFACTS both require repository read credentials, but ANNOTATIONS are public.
  Without this, a red end-to-end job tells an outside contributor (and any tooling) only
  "Process completed with exit code 1", and the actual assertion — the one piece of information
  that matters — is unreachable. Emitting each failure as `::error::` puts the test name and its
  message on the run summary and into the public annotations API.

  Runs on both targets: pwsh is present on the Windows and Linux hosted runners alike.
#>
[CmdletBinding()]
param(
  [string] $Path = '.',
  [int] $MaxAnnotations = 40
)

$ErrorActionPreference = 'Stop'

$files = Get-ChildItem -Path $Path -Filter *.trx -Recurse -ErrorAction SilentlyContinue
if (-not $files) {
  Write-Host "::warning::No .trx files found under '$Path' - the test host probably failed before it could run anything."
  exit 0
}

$emitted = 0
foreach ($file in $files) {
  Write-Host "Reading $($file.FullName)"
  [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw

  $results = $trx.TestRun.Results.UnitTestResult
  if (-not $results) { continue }

  foreach ($result in $results) {
    if ($result.outcome -eq 'Passed') { continue }
    if ($emitted -ge $MaxAnnotations) {
      Write-Host "::warning::More than $MaxAnnotations failures; the rest are in the .trx artifact."
      exit 0
    }

    $name = $result.testName
    $message = $result.Output.ErrorInfo.Message
    $stack = $result.Output.ErrorInfo.StackTrace

    # annotations are single-line: fold newlines so the whole message survives
    $detail = (("$message`n$stack") -replace "`r", '' -replace "`n", ' | ').Trim()
    if ($detail.Length -gt 900) { $detail = $detail.Substring(0, 900) + ' [...]' }

    if ($result.outcome -eq 'Failed') {
      Write-Host "::error title=$name::$detail"
    } else {
      Write-Host "::warning title=$name ($($result.outcome))::$detail"
    }

    $emitted++
  }
}

if ($emitted -eq 0) {
  Write-Host '::warning::The job failed but no individual test did - look at the build or host startup instead.'
}
