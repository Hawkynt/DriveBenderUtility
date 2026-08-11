# Open issues

Verified against the working tree, not inherited from an older audit. Each item names the file it
lives in so the next pass can start from evidence instead of prose.

## Closed since the last audit

The whole former "Tier 0 / Tier 1" set is gone. Real fsync barriers and streaming whole-file
operations (`6fdcd8c`), staged-write supersede and cache coherence (`f5c6d18`), marker checks on
destructive paths (`dc597b3`), XSS escaping, MSI signature verification and the channel-directory
ACL (`51e5076`), rename-onto-self and the smartctl pipe deadlock (`d29d3fa`), stale journal mirrors
(`8b3ad3a`), the journal no longer mirrored to whole-file remotes (`d6186f5`), the remote object
read cache (`d065e93`), per-member serialization of non-thread-safe stores (`f85cf3b`), physical-disk
failure domains (`48f3339`), manifest highest-version-wins on mount (`fc64352`), the daemon
robustness pass (`053e9a6`), and the background-job locking pass (`d590351`).

Closed in the current pass:

- **A read could report an acknowledged write as a 0-byte file — and cache that.**
  `GetAttributes` derived the logical length from two reads (the durable stat of one copy plus the
  write buffer's overlay of the bytes still owed to the others) while holding no lock, so a
  `FlushPath` in between yielded a length *shorter* than an acknowledged write. This was the root
  cause of the intermittent `ConcurrencyFuzzTests` read-your-writes failure, which reproduced about
  once in twenty-four full-suite runs. Both reads now happen under a shared path lease.
- **Directory listings ignored the write buffer entirely**, so a file with owed bytes listed at its
  stale on-disk size even with nothing racing.
- **A copy that was merely BEHIND silently truncated the file**: the length came from `copies[0]`,
  whatever member resolved first. Metadata now resolves to the newest copy by last-write time.
- **A lagging copy's short block was cached and served**, hiding the lag behind a cache hit for the
  whole TTL. The read path fails over, and a short answer is never cached.
- **A remote whose transport dropped reported its files as ABSENT** (`_TryQuery` swallowed every
  exception into `false`), so `Unlink` skipped that member's copy — a ghost that resurrects.
- **A member that dropped out DURING a delete kept its copy with nothing recorded**; every member
  that fails a delete is now tombstoned for replay on its return.
- **A whole-pool cache invalidation could be undone by an in-flight prefetch** — `InvalidatePool`
  replaced the shard, restarting the epoch at zero and letting a captured epoch-zero `PutIfCurrent`
  through.
- **Performance**: folder config resolution cost 16.9 KB and 110 µs *per call*, twice per write
  (now 136 B / 248 ns); glob matching rebuilt a regex per call; block routing allocated per block;
  `ResolveCopies` rebuilt its list on every cache hit; the activity feed took a process-wide lock on
  the drop path of every read and write; the page cache served all pools from one lock.
- **Blocking remote I/O ran on the shared thread pool**, so a burst of cloud reads starved the rest
  of the process — including the management daemon. It now has its own bounded, on-demand threads.
- **Media surgery, purge and driver install ran inline in the HTTP handler** for up to thirty
  minutes. They join the job model, with progress on the SSE frame and cancellation.

## Still open

### Threading / throughput

1. **Sync-over-async across every cloud provider.** All 13 stores in `Hawkynt.CloudStorage/Stores/*`
   block on `.GetAwaiter().GetResult()`. Full async is blocked by the driver callbacks (WinFsp/
   Dokan/FUSE are synchronous — see `WinFspAdapter.cs:145`). *Contained*: the engine now routes work
   touching a member that declares `IVolumeIO.BlocksCallingThread` onto `BlockingIoScheduler`, so
   those blocked calls can no longer drain the shared pool. The stores themselves are still
   sync-over-async.
2. **No provider-level range reads.** A remote read still fetches whole objects; the bounded LRU
   object cache collapses a file's per-block burst into one download, but an object larger than the
   cache's per-entry budget still degrades. Real range reads need extending the CloudStorage library
   across all 13 providers.
3. **Whole-object RAM spikes remain in `_MoveTree`** (`Upload(target, Download(source))` per file,
   `DriveBender.Backends/WholeFileStore.cs`). Bounded at 2 GiB by `MaxFileSize`, but still a 2 GiB
   spike, and inherent to an `IWholeFileStore` contract whose only primitives are `byte[]` in and
   out — fixing it means adding streaming methods to all 13 providers. `Truncate` no longer spikes
   for the common truncate-to-zero (every publish starts with one).

### UI

4. **Jobs relayed into a MOUNTED pool's own process cannot be cancelled** — the operation runs in
   that process, and the manager has no channel to call it back. Such jobs correctly report
   `cancellable: false` rather than offering a button that does nothing, but the capability itself
   is missing: `MountRegistry` would need a cancel op alongside `RequestOp`.
5. **Progress is per-job, not per-item.** A worker's latest stdout line is surfaced; there is no
   structured "N of M files" percentage for a long scatter or scrub.

## Standing guards

These now fail the build rather than needing to be re-found:

- `ConcurrencyStressTests` — many readers against many writers on the same paths, opposing renames,
  create/delete churn; a self-describing content scheme detects a torn read, per-reader monotonicity
  detects a vanished write, a watchdog names any stuck worker and the operation it was inside, and
  budgets bound retained heap, allocation, idle pump work and CPU-per-wall-clock.
- `CrashConsistencyTests` — write, delete, rename and create each interrupted at **every** volume
  operation they perform, with a guard that fails if a case sits past the operation's real length
  and therefore tests nothing.
- `EnginePerformanceTests` — allocation budgets for folder-config resolution, block routing, the
  activity-feed drop path, cached reads and write staging.
- `BlockingIoSchedulerTests`, `JobRegistryTests`, `MetadataCoherenceTests`, `DataSafetyTests`,
  `PageCacheTests` — the isolation, UI-responsiveness, coherence and accounting invariants above.
