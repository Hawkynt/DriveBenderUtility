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

- **Mounting on Windows was impossible.** WinFsp's mount-point grammar has no trailing separator
  (`X:`), while the manifest and CLI hold `X:\`; WinFsp rejected it with STATUS_OBJECT_NAME_INVALID
  and `winfsp.net` then called `FspFileSystemSetMountPointEx` on a filesystem it had never created,
  killing the process with an access violation and no message. Normalised, with `Preflight`
  consulted first so a bad mount point is a sentence rather than a crash.
- **Every newly written file stayed on disk as `*.TEMP.$DRIVEBENDER`** and was swept as an
  incomplete write by the next mount. Staged publication hung off WinFsp's `Close`, but `Cleanup`
  is "the application closed its last handle" while `Close` is "the kernel released the file
  object", which the FSD defers — often to unmount. A non-delete cleanup now flushes.
- **The end-to-end harness raced the Linux mount.** On Linux the mountpoint is a directory the
  harness creates, so it exists and is writable before FUSE attaches to it; a readiness check that
  only looked for the path passed immediately and the test then wrote into the plain directory
  UNDERNEATH the mount. The data read back perfectly and no member ever saw it, which is exactly
  the "written data never reaches the members" failure this file previously recorded as a product
  defect. It was the harness. Readiness now requires the kernel to report a mount at that path.

- **A recursive delete skipped files and then failed as "not empty".** Directory enumeration
  resumed by SEARCHING for the marker entry, which the caller had just deleted, so the whole
  listing was consumed and enumeration ended early. Resumption is by name order now.

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

An earlier revision of this file claimed "nothing known" here. That was wrong — it dropped the two
UI items below, which were open then and are open now. "Nothing known" is a claim to be re-earned
after each pass, not a state to reach.

1. **Jobs relayed into a MOUNTED pool's own process cannot be cancelled.** The operation runs in
   that process and the manager has no channel to call it back, so such jobs report
   `cancellable: false` — honest, but the capability is missing. `MountRegistry` needs a cancel op
   alongside `RequestOp`.
2. **Progress is per-job, not per-item.** The worker's latest stdout line is surfaced; there is no
   structured "N of M files" for a long scatter or scrub.
3. **A file replaced by rename keeps serving its OLD content to new readers — Windows.** After
   `File.Move(source, target, overwrite: true)` succeeds, a reader that opens the target path
   afresh still gets the pre-replacement bytes. Measured through a real WinFsp mount: six
   replacements landed and the file settled on version 40, while all 3,200 reads taken during the
   run returned version 1 — so the replacements were real and every reader missed all of them.
   Atomic replace-by-rename is the pattern careful software uses precisely to publish a new
   version safely, so this makes the safe pattern the broken one. Suspect the adapter passing
   `fileNode = null` on every open, which leaves WinFsp unable to tell "same file" from "different
   file now at this name" and lets the cached section for the name survive the replace. Pinned by
   `SharedAccessEndToEndTests.SharedFile_GivenWritersReplacingItByRename_...`, which is `[Ignore]`d
   with this reason rather than weakened, so the scenario is not lost.
4. **A returning member is not healed back to its duplication level.** A file written while one
   member was offline stays single-copy after that member comes back — waited two minutes with the
   background pump running, through a real mount. The degraded write itself succeeds (SAFE-DEGRADE
   works) and a file deleted while the member was away correctly does NOT resurrect (tombstone
   replay works), so the gap is specifically owed-copy heal on member return. Pinned by
   `MemberLossEndToEndTests.Eject_GivenAMemberIsAway_ThenWritesStillSucceedAndHealWhenItReturns`,
   `[Ignore]`d with this reason rather than weakened.

   Noticed alongside it: the engine RECREATES a missing member's root directory while it is away,
   writing pool scaffolding to the path the disk used to occupy. On a real machine that is data
   landing on whatever filesystem hosted the mount point instead of on the disk — worth deciding
   deliberately rather than by accident.
5. **The landing-zone drainer does not appear to move data down.** A file written to a pool with a
   landing-zone member is still absent from the capacity member two minutes later, with the
   background pump running, so the fast tier is never freed either. Not yet distinguished between
   "the drainer never ran" and "the landing role was not applied to the member by `pool-create -l`"
   — both are worth checking before assuming which. Tiering is otherwise TRANSPARENT, which is the
   part that matters most: a file stays readable and writable throughout, verified by
   `TieringEndToEndTests.Tiering_WhileTheMoverIsRelocatingFiles_...` with six threads rewriting
   while the mover works.
6. **`HandleTable.RenameSubtree` has the same shape as the `RenamePath` defect fixed in `4ad2094`**
   — it re-keys children with `_files[file.Path] = file`, clobbering any state already there — and
   `_RenameFolder` holds leases on the two folder paths but on **no child file** while moving them.
   Not reproduced end to end; flagged because it is the sibling of a bug that was proven real, and
   the stress suite races file renames only, which is why it was not caught.

The three throughput items that stood here — sync-over-async across the providers, the absence of
provider-level range reads, and the whole-object RAM spikes — are closed above.

## Closed in the provider pass

- **No provider-level range reads.** Every remote read fetched a whole object, so the engine's
  block-by-block reads either re-downloaded the file per block or (behind the object cache) once
  per file — 1 GiB transferred to serve 128 KiB either way. `ICloudStore` now carries
  `OpenReadRange`, implemented natively by twelve of the thirteen providers (S3, Azure Blob, Azure
  Files, GCS, Google Drive, OneDrive, Box, Yandex, HiDrive, WebDAV via the HTTP `Range` header;
  FTP via the REST restart offset; SFTP by seeking). Dropbox's SDK exposes no ranged download, so
  it keeps the whole-object fallback and says so through `CloudCaps`. `WholeFileVolumeIO` serves
  reads from a windowed range reader where the capability is present, and from the object cache
  where it is not — the fallback is still correct, just not cheap, which is the whole point of
  declaring the difference.
- **Whole-object RAM spikes.** `Upload`/`Download` were `byte[]`-only, so a subtree move
  (`_MoveTree`) held each file whole in memory and a shrink held the pre-truncation object. Both
  stream now; a shrink spools its surviving prefix (to disk past 8 MiB) because the read and the
  write target the same object and must not overlap. The write-staging buffer streams out on flush
  instead of being copied with `ToArray()`, which had briefly held the object twice.
- **Sync-over-async.** Every blocking SDK call now goes through one audited bridge that escapes a
  captured `SynchronizationContext` before waiting — `GetAwaiter().GetResult()` on a task whose
  continuation is posted back to the blocked thread deadlocks outright rather than merely being
  slow. True async end to end remains impossible while the driver callbacks (WinFsp/Dokan/FUSE)
  are synchronous; what is fixed is that the wait is safe, contained, and off the shared thread
  pool (`BlockingIoScheduler`).

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
- `RemoteRangeReadTests` — counts what actually crosses the store boundary: a partial read of a
  large object moves a fraction of it with ranges and all of it without, a block-by-block read
  never asks for the whole object, a subtree move streams every file, a shrink never fetches the
  pre-truncation object, and the range-free fallback stays correct.
- `SyncBridgeTests` — blocking under an installed single-threaded synchronization context
  completes instead of deadlocking, and a provider failure surfaces as itself rather than wrapped.
- **`DriveBender.EndToEnd.Tests`** — the SHIPPED `dbmount` binary, with no project reference to the
  engine, on both targets in CI: a real pool mounted through WinFsp/Dokan or FUSE and driven
  through `System.IO`; the management API over HTTP; and the page itself in Chromium. This tier
  exists because the engine suite drives an in-memory fake and therefore stayed green through
  `5b67a05`, in which mounting any local pool was impossible. `DBE2E_REQUIRE_DRIVER=1` makes a
  missing driver a failure, so the suite cannot report green by skipping everything.
