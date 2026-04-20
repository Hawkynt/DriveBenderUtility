# CI/CD Pipeline — DriveBenderUtility

> Everything in this folder is the automated pipeline. Workflows live here, scripts live in `scripts/`.

## Files

| File                            | Trigger                             | Purpose                                 |
|---------------------------------|-------------------------------------|-----------------------------------------|
| `ci.yml`                        | push + PR + `workflow_call`         | Build + tests (windows-only, net47)     |
| `release.yml`                   | tag push `v*`                       | GitHub Release (Console + UI zips)      |
| `nightly.yml`                   | CI success on `master`              | `nightly-YYYY-MM-DD` + GFS prune        |
| `_build.yml`                    | `workflow_call` (internal)          | .NET Framework publish + zip            |
| `scripts/*`                     | invoked by workflows                | version/changelog/prune tools           |

## Why windows-only

All projects target `net47` (.NET Framework 4.7). Publishing framework-dependent output requires the .NET Framework targeting pack, only available on Windows runners. There is no `--self-contained` equivalent for .NET Framework; users must install the .NET Framework 4.7 Runtime.

## Release artifacts

| Artifact                                   | Produced by          | Runtime requirement     |
|--------------------------------------------|----------------------|-------------------------|
| `DriveBender-Console-win-<version>.zip`    | release + nightly    | .NET Framework 4.7      |
| `DriveBender-UI-win-<version>.zip`         | release + nightly    | .NET Framework 4.7 + WPF|
