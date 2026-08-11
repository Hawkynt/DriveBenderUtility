# Open issues

Verified against the working tree, not inherited from an older audit. The previous version of
this file listed roughly twenty defects that had already been fixed — everything below was
re-checked against the actual code before being written down, and each item names the file it
lives in so the next pass can start from evidence instead of prose.

## Closed since the last audit

The whole former "Tier 0 / Tier 1" set is gone. Real fsync barriers and streaming whole-file
operations (`6fdcd8c`), staged-write supersede and cache coherence (`f5c6d18`), marker checks on
destructive paths (`dc597b3`), XSS escaping, MSI signature verification and the channel-directory
ACL (`51e5076`), rename-onto-self and the smartctl pipe deadlock (`d29d3fa`), stale journal mirrors
(`8b3ad3a`), the journal no longer mirrored to whole-file remotes (`d6186f5`), the remote object
read cache (`d065e93`), per-member serialization of non-thread-safe stores (`f85cf3b`), physical-disk
failure domains (`48f3339`), manifest highest-version-wins on mount (`fc64352`), and the daemon
robustness pass (`053e9a6`).

Closed in the current pass:

- **Background jobs bypassed the per-file lock.** `FlushPath`, `DrainOneLandingFile`, `_HealOne`
  and `_PublishStaged` mutated copies with only an `IsDirty`/`IsOpen` TOCTOU check between them
  and a foreground writer. `HandleTable` now hands out `PathLease` locks that do not require an
  open handle, and every background mutator plus `Unlink`/`Rename`/`Create`/`SetAttributes` takes
  one. Reproduced first as a crash (`Short read at block 1`) in the new `ConcurrencyFuzzTests`.
- **`ServeCommand._RunWorker`/`_RunWorkerText` could deadlock**, reading stdout to completion
  before touching stderr, and never killed a worker that overran its 30-minute budget. Both now
  share `_RunWorkerCore`, which drains both pipes concurrently and kills on timeout.
- **A deep scan or restore held the browser's HTTP request open for its whole run.** They now
  return a job ticket immediately (`/api/job`); measured at 26 ms versus a request that previously
  stayed open for the duration of the scan.
- **`PageCache.InvalidatePath` scanned every cached block in the pool** on every mutation, and the
  eviction loop re-summed all shards per iteration. Both are now indexed/incremental.
- **Whole-file remote publish downloaded the object it was about to discard** — the read-modify-write
  preload is lazy, so a `SetLength(0)` (what every publish does first) skips the download entirely.
- **`WholeFilePublisher.CopyCounted` allocated a 1 MiB LOH buffer per call**; it rents now.
- **The write payload was copied twice** (`data.ToArray()` then a defensive clone in `StageWrite`).

## Still open

### Threading / throughput

1. **Sync-over-async across every cloud provider.** All 13 stores in `Hawkynt.CloudStorage/Stores/*`
   block on `.GetAwaiter().GetResult()`. The engine calls them from `Parallel.ForEach` in
   `PoolFileSystem._Prefetch` and `_ReadRange`, so a cloud member parks several thread-pool threads
   per read burst. Full async is blocked by the driver callbacks (WinFsp/Dokan/FUSE are
   synchronous — see `WinFspAdapter.cs:145`). The contained fix is a bounded, dedicated scheduler
   for remote members so blocked cloud calls can never drain the shared pool.
2. **No provider-level range reads.** A remote read still fetches whole objects; the bounded LRU
   object cache collapses a file's per-block burst into one download, but an object larger than the
   cache's per-entry budget still degrades. Real range reads need extending the CloudStorage library.
3. **Whole-object RAM spikes remain** in `WholeFileVolumeIO.Truncate` (downloads the object to
   resize it) and `_MoveTree` (`Upload(target, Download(source))` per file), both in
   `DriveBender.Backends/WholeFileStore.cs`. Bounded at 2 GiB by `MaxFileSize`, but that is still a
   2 GiB spike.

### Correctness / robustness

4. **`_LoadBlock` caches a short block without cross-checking other copies**
   (`PoolFileSystem.cs`). Currently unreachable — the mount-time OOB `QuickScan` repairs a lagging
   copy before it can serve anything (pinned by `Read_GivenOneCopyLaggingBehind…` in `FuzzTests`) —
   but the read path itself has no failover for a copy that exists and is merely behind.
5. **`WholeFileVolumeIO.IsOnline` mutates its probe cache without synchronization**
   (`WholeFileStore.cs`): `_lastProbeUtc`/`_lastProbeResult` are read and written from concurrent
   callers. Benign in practice (a redundant probe), but it is a data race.
6. **`FileExists`/`FolderExists` on remote backends swallow every exception** via `_TryQuery` and
   return false, so a transport failure is indistinguishable from "not there" — the same class of
   misreporting that `Translate` was introduced to avoid for the other operations.

### UI

7. **Media operations still run inline in the HTTP handler.** `_MediaOp` (scatter-remove, replace)
   spawns a worker and blocks the request for up to 30 minutes. Health and restore were converted
   to the job model; these should follow the same path.
8. **Jobs are poll-only.** `/api/job` works and the dialog reports elapsed time, but progress is not
   published on the existing SSE frame and a running job cannot be cancelled.
