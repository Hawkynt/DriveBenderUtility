<#
.SYNOPSIS
  Regenerates docs/EndToEndCoverage.md from the end-to-end .trx results of both targets.

.DESCRIPTION
  One row per end-to-end test, with what it covers and how it fared on Windows and on Linux.
  The point is that the table is DERIVED, never hand-maintained: a hand-written matrix drifts the
  moment a test is added or starts failing, and then quietly misleads. This runs in CI after both
  end-to-end jobs, so the committed document always reflects the last real run on real drivers.

  A test's description comes from its [Description] attribute, read from the SOURCE rather than the
  .trx — the trx logger records outcomes and drops descriptions entirely. Without one the row falls
  back to the test's own name, which the Given/When/Then convention already makes readable. An
  [Ignore] reason is carried into the row too, so a held-back scenario says why.

.PARAMETER WindowsTrx
  Path to (or a directory containing) the Windows end-to-end .trx.

.PARAMETER LinuxTrx
  Path to (or a directory containing) the Linux end-to-end .trx.

.PARAMETER Output
  The markdown file to write.
#>
[CmdletBinding()]
param(
  [string] $WindowsTrx = '',
  [string] $LinuxTrx = '',
  [string] $Output = 'docs/EndToEndCoverage.md'
)

$ErrorActionPreference = 'Stop'

function Read-Results([string] $path) {
  $results = @{}
  if (-not $path) { return $results }

  $files = if (Test-Path -PathType Container $path) {
    Get-ChildItem -Path $path -Filter *.trx -Recurse -ErrorAction SilentlyContinue
  } elseif (Test-Path $path) {
    ,(Get-Item $path)
  } else {
    @()
  }

  foreach ($file in $files) {
    [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw

    # definitions carry the description and the fully-qualified name; results carry the outcome
    $descriptions = @{}
    foreach ($definition in $trx.TestRun.TestDefinitions.UnitTest) {
      if (-not $definition) { continue }
      $descriptions[$definition.id] = [pscustomobject]@{
        Class       = $definition.TestMethod.className
        Description = $definition.Description
      }
    }

    foreach ($result in $trx.TestRun.Results.UnitTestResult) {
      if (-not $result) { continue }
      $meta = $descriptions[$result.testId]
      $results[$result.testName] = [pscustomobject]@{
        Outcome     = $result.outcome
        Class       = if ($meta) { ($meta.Class -split '\.')[-1] } else { '' }
        Description = if ($meta) { $meta.Description } else { '' }
        Message     = $result.Output.ErrorInfo.Message
      }
    }
  }

  return $results
}

# The .trx carries outcomes and nothing else — no [Description], whatever the logger's schema
# suggests — so the prose for each row is read from the tests themselves. That is the better source
# anyway: it is where the author wrote it, and it survives a run that never produced a .trx.
function Read-Annotations([string] $sourceRoot) {
  $annotations = @{}
  if (-not (Test-Path $sourceRoot)) { return $annotations }

  foreach ($file in Get-ChildItem -Path $sourceRoot -Filter *.cs -Recurse) {
    $text = Get-Content -LiteralPath $file.FullName -Raw

    foreach ($match in [regex]::Matches($text, 'public\s+void\s+(\w+)\s*\(')) {
      $name = $match.Groups[1].Value
      $start = [Math]::Max(0, $match.Index - 2000)
      $preamble = $text.Substring($start, $match.Index - $start)

      # only the attributes of THIS method: everything after the previous method's closing brace
      $lastEnd = $preamble.LastIndexOf('}')
      if ($lastEnd -ge 0) { $preamble = $preamble.Substring($lastEnd + 1) }

      $description = ([regex]::Match($preamble, '\[Description\(\s*(?<text>"(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)\s*\)\]')).Groups['text'].Value
      $ignore = ([regex]::Match($preamble, '\[Ignore\(\s*(?<text>"(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)\s*[,)]')).Groups['text'].Value

      if ($description -or $ignore) {
        $annotations[$name] = [pscustomobject]@{
          Description = Join-Literal $description
          Ignore      = Join-Literal $ignore
        }
      }
    }
  }

  return $annotations
}

# turns a C# literal, possibly a chain of "a" + "b", into its text
function Join-Literal([string] $literal) {
  if (-not $literal) { return '' }
  $joined = -join ([regex]::Matches($literal, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { $_.Groups[1].Value })
  return ($joined -replace '\\"', '"' -replace '\\\\', '\')
}

function Format-Outcome($result) {
  if (-not $result) { return 'not run' }
  switch ($result.Outcome) {
    'Passed'      { 'pass' }
    'Failed'      { '**FAIL**' }
    'NotExecuted' { 'skipped' }
    default       { $result.Outcome }
  }
}

function Humanise([string] $name) {
  # "Eject_GivenAMemberIsPulled_ThenFilesStayReadable" -> "Given a member is pulled, then files stay readable"
  $parts = $name -split '_'
  $tail = if ($parts.Count -gt 1) { $parts[1..($parts.Count - 1)] -join ' ' } else { $name }
  $spaced = [regex]::Replace($tail, '(?<=[a-z0-9])(?=[A-Z])', ' ')
  $spaced = $spaced -replace '\bGiven\b', 'given' -replace '\bThen\b', ', then' -replace '\bWhen\b', ', when'
  return $spaced.Substring(0, 1).ToUpper() + $spaced.Substring(1)
}

$annotations = Read-Annotations (Join-Path $PSScriptRoot '../../../DriveBender.EndToEnd.Tests')
$windows = Read-Results $WindowsTrx
$linux = Read-Results $LinuxTrx

$names = @($windows.Keys) + @($linux.Keys) | Sort-Object -Unique
if (-not $names) {
  Write-Host '::warning::No end-to-end results found; leaving the coverage matrix untouched.'
  exit 0
}

$rows = foreach ($name in $names) {
  $w = $windows[$name]
  $l = $linux[$name]
  $meta = if ($w) { $w } else { $l }
  $annotation = $annotations[$name]

  $description = if ($annotation -and $annotation.Description) { $annotation.Description }
                 elseif ($meta.Description) { $meta.Description }
                 else { Humanise $name }

  # a held-back scenario is only honest if the reason travels with it — a bare "skipped" reads as
  # "not applicable here" when it often means "this is a known defect we have not fixed"
  if ($annotation -and $annotation.Ignore) {
    $description = "$description _(held back: $($annotation.Ignore))_"
  }

  [pscustomobject]@{
    Area        = $meta.Class -replace 'EndToEndTests$', ''
    Test        = $name
    Description = $description
    Windows     = Format-Outcome $w
    Linux       = Format-Outcome $l
  }
}

$builder = [System.Text.StringBuilder]::new()
[void] $builder.AppendLine('# End-to-end coverage')
[void] $builder.AppendLine()
[void] $builder.AppendLine('What the shipped `dbmount` binary is actually exercised against, on both targets, through a')
[void] $builder.AppendLine('real filesystem driver and a real browser.')
[void] $builder.AppendLine()
[void] $builder.AppendLine('**This file is generated** by `.github/workflows/scripts/e2e-matrix.ps1` from the end-to-end')
[void] $builder.AppendLine('`.trx` results of the Windows and Linux CI jobs. Do not edit it by hand — a hand-kept matrix')
[void] $builder.AppendLine('drifts the moment a test is added or starts failing, and then quietly misleads.')
[void] $builder.AppendLine()
[void] $builder.AppendLine("Generated from run: $(if ($env:GITHUB_RUN_ID) { "[$env:GITHUB_RUN_ID](https://github.com/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID)" } else { 'a local run' }).")
[void] $builder.AppendLine()

$passed = @($rows | Where-Object { $_.Windows -eq 'pass' -or $_.Linux -eq 'pass' }).Count
$failed = @($rows | Where-Object { $_.Windows -like '*FAIL*' -or $_.Linux -like '*FAIL*' }).Count
[void] $builder.AppendLine("$($rows.Count) scenarios — $passed passing on at least one target, $failed failing.")
[void] $builder.AppendLine()
[void] $builder.AppendLine('| Area | Scenario | What it covers | Windows | Linux |')
[void] $builder.AppendLine('| --- | --- | --- | :---: | :---: |')

foreach ($row in $rows | Sort-Object Area, Test) {
  $description = ($row.Description -replace '\|', '\|') -replace '\s+', ' '
  [void] $builder.AppendLine("| $($row.Area) | ``$($row.Test)`` | $description | $($row.Windows) | $($row.Linux) |")
}

[void] $builder.AppendLine()
[void] $builder.AppendLine('`skipped` marks a scenario the platform cannot express or one deliberately held back against a')
[void] $builder.AppendLine('known defect — the reason travels with the test, in its `Assert.Ignore`/`[Ignore]` text.')

$directory = Split-Path -Parent $Output
if ($directory -and -not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
Set-Content -LiteralPath $Output -Value $builder.ToString() -NoNewline
Write-Host "Wrote $Output with $($rows.Count) scenarios."
