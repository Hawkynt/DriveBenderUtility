# CI/CD Pipeline — DriveBenderUtility

> Everything in this folder is the automated pipeline. Workflows live here, scripts live in `scripts/`.

## Files

| File            | Trigger                                                        | Purpose                                       |
|-----------------|----------------------------------------------------------------|-----------------------------------------------|
| `ci.yml`        | pull request to `main`, `workflow_call`, dispatch, weekly cron | Build, tests, end-to-end drivers, benchmark   |
| `release.yml`   | tag push `v*`                                                   | GitHub Release (Console + App zips)           |
| `nightly.yml`   | CI success on `main`                                            | `nightly-YYYYMMDD` + GFS prune                |
| `_build.yml`    | `workflow_call` (internal)                                      | Publish + zip the shipped artifacts           |
| `scripts/*`     | invoked by workflows                                            | version, changelog, prune, matrix, annotations |

## What `ci.yml` runs

| Job            | Runner           | What it covers                                                     |
|----------------|------------------|--------------------------------------------------------------------|
| `test`         | `windows-latest` | Build and unit tests                                                |
| `e2e-windows`  | `windows-latest` | End-to-end against the WinFsp driver and the UI                     |
| `e2e-linux`    | `ubuntu-latest`  | End-to-end against the FUSE driver and the UI                       |
| `e2e-matrix`   | `ubuntu-latest`  | Collates both end-to-end legs and publishes `docs/EndToEndCoverage.md` |
| `benchmark`    | `windows-latest` | Performance run publishing `docs/Performance.md`                     |

The benchmark does not gate a pull request. It is far too heavy for that, and a number nobody
regenerates is worse than no number, so it runs on the weekly cron or on an explicit dispatch.

## Where each target framework goes

Most of the tree is `net10.0`. Two projects remain on `net47` — `DriveBender.Console` and the unit
test assembly beside it — and `Hawkynt.CloudStorage` multi-targets `netstandard2.0;net47`.

Publishing framework-dependent `net47` output needs the .NET Framework targeting pack, which exists
only on Windows runners, so the Console artifact is built there. The driver work is not
Windows-only: the pool mounts through WinFsp on Windows and FUSE on Linux, and both are exercised
end to end on their own runners.

## Release artifacts

| Artifact                                    | Produced by       | Runtime requirement           |
|---------------------------------------------|-------------------|-------------------------------|
| `DriveBender-Console-win-<version>.zip`     | release + nightly | .NET Framework 4.7            |
| `DriveBender-App-win-<version>.zip`         | release + nightly | .NET 10 desktop runtime       |

Versions come from files rather than git tags: `scripts/version.pl` derives them from the commit
count, so a checkout at any commit knows its own version without needing the tags fetched.
