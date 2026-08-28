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


### Resolved this pass: files written by a long-running application were never drained or healed

One defect, two symptoms, and two wrong diagnoses on the way to it.

**The defect.** `HandleTable.IsOpen` counted kernel file objects. WinFsp sends CLEANUP when the
application closes its last handle and CLOSE only when the kernel releases the file object, and the
FSD defers CLOSE for a long time — often until unmount. The drainer and the healer both skip open
files, on the sound principle that a file someone is actively writing should converge through the
write path. So an ordinary file, written and closed by a program that keeps running, stayed
permanently "open": never drained to capacity, and — with duplication on — never given its owed
second copy. That is a durability loss and not a delay. The window is the lifetime of the writing
application, which for a service is forever.

`Cleanup` in the WinFsp adapter now tells the engine the application is done
(`IPoolFileSystem.MarkApplicationClosed`), while the handle stays valid for whatever the kernel
still sends against it. FUSE's `release` is already a prompt close, so the Linux adapter needs
nothing; `Close` implies the same thing where no cleanup ever arrives.

**How the diagnosis went wrong.** First it was recorded as "the drainer does not move data down"
with the landing role itself in doubt — the role was fine. Then, after reproducing the drain
working five different ways outside the test suite, it was recorded as "the drainer was never
broken; the suite is at fault". That second claim was also wrong, and worse, because it blamed the
tests for being right. Every one of those probes wrote from a SHORT-LIVED process (or from Python,
which behaves the same way here), and process exit forces the file object down — which is exactly
the condition that hides the bug. The reproducer that finally settled it was a .NET
`File.WriteAllBytes` from a process that stays alive: never drained, outside the suite, every time.
The lesson is specific and worth keeping: when a probe disagrees with a test, the probe is a
hypothesis too, and the difference between them IS the evidence.

Closed by this: the two landing-zone drain scenarios, the owed-copy heal on member return, and the
member-returns-while-io-is-in-flight scenario — all four were held back and all four now pass. The
end-to-end suite also dropped from 5m09s to 2m25s, because those tests no longer sit out two-minute
timeouts.

### Bit-rot: two of three fixed, and the remaining one is a format decision

The battery had no bit-rot scenario at all, which was a striking gap for a product whose central
promise is holding two copies. `BitRotEndToEndTests` adds one: the bytes of ONE copy are altered on
the member behind the pool's back and the timestamps are then restored, because real rot does not
update mtime — so a pool that resolves conflicts by comparing times cannot tell the good copy from
the rotten one, and only content can.

The classification in `IntegrityService` turned out to be sound already — it separates rot (content
changed, metadata did not) from an edit (both changed) and repairs the former from a
checksum-verified copy. Every branch of it hangs on one condition: the copy must HAVE a recorded
checksum. It never did.

**Fixed — a deep health check now establishes the baseline it needs.** Checksums for ordinary files
are not written by the write path, and the baseline recording in the scrub was guarded by
`if (!detectOnly)`, so `pool-health --deep` computed every hash and then threw them away. A pool
that was only ever health-checked could therefore never acquire a baseline, and rot stayed
indistinguishable from an edit forever. The baseline is now recorded on a detect-only pass too, but
ONLY when every copy of a file agrees — the one moment its content is not in question — and it
touches the checksum sidecar, never pool data. With that in place, `--fix` repairs a rotted copy
from the good one, and rot on EVERY copy is reported as unrecoverable instead of `healthy (deep
scan)`. Both scenarios pass.

**Still open — a scrub of a MOUNTED pool loses its baseline.** `pool-health` runs in its own
process and writes the checksum sidecar; the mounted engine holds the same sidecar and saves its
own view over the top on unmount, so the baseline silently disappears. That is why the repair
scenarios only pass with the scrub run while nothing is mounted, which is what they now do. For a
user this means the obvious action — health-check the pool you have mounted — achieves nothing
durable. The two processes need to agree on ownership of that file.

**Decided, not open — reads are trusted and are not verified.** Hashing every block served would
trade the pool's throughput away for a check that nearly always passes. The bargain instead is:
writes record what they can and MARK what they cannot, and a scheduled sweep re-baselines the marks
and finds the damage. Marking rather than deleting is what makes the write side cheap — a
positional write cannot rehash a whole file without making random access cost O(file) per
operation, and a file rewritten continuously marks its entry ONCE instead of dirtying the checksum
database on every write. A stale entry is read as no entry at all, because trusting a hash that
predates a known write would report a good file as rotted and the "repair" would then overwrite
newer content with older — an integrity check turning into data loss.

A checksum also stops being trustworthy WITHOUT the engine seeing a write: an external edit, a
restored backup, a sync tool. The entry then looks perfectly fresh while its hash describes content
that no longer exists. A scan now infers that from the file's size and mtime no longer matching what
the entry recorded, and flags the entry in the database as well as ignoring it — so the conclusion
is stored rather than re-derived by every later scan, and a scan interrupted before it reconciles
leaves nothing behind that still looks authoritative.

The entry is still handed to the classifier for that pass, though, and that is deliberate: it is
what distinguishes "this copy changed behind our back" from "we never had a baseline", and that
distinction is what PRESERVES A CONFLICT. Withholding it turned two copies edited externally to
different content — which are kept for the user to resolve — into "newest timestamp wins,
overwrite the other". Measured, not theorised: `Scrub_GivenDivergentEditsOnBothCopies_...` caught it.

Measured cost of the whole arrangement, on a 2112 MiB file through a real mount: 424 MiB/s written,
2326 MiB/s read, and the last 128 MiB of the file writes as fast as the first (0.30s against
0.35s) — a per-write cost that scaled with file size, which is what rehashing on every write would
produce, would show up exactly there.

`integrity.scrubberSchedule` and `deepScrubSchedule` now have a job behind them (`ScrubJob`);
before this they were decoration, with nothing in the scheduler that read them. Cadences accept the
`idle-weekly` forms the default configuration uses and plain durations alike, an unreadable value
degrades to sweeping rather than to silence, and `off`/`never`/`manual` disable it.

**Known consequence of that decision.** A silently damaged copy is served to the application even
though an intact one sits on the other member, and the caller gets no error, so it believes the
bytes and writes them onward. This is not a quick fix and should not be treated as one: the
database holds WHOLE-FILE hashes while a read serves a BLOCK, so there is nothing to check a block
against without per-block checksums — a format change with a real throughput and space cost. What
exists today is detect-and-repair at scrub time, and the exposure is the window before a scrub
runs. Pinned by `BitRotEndToEndTests.BitRot_GivenOneCopyIsSilentlyDamaged_...`, held back rather
than weakened.

### Measured, and one of the numbers is a problem: cached random reads do not scale with threads

`PerformanceMatrixEndToEndTests` prices the pool per tier, per concurrency, per file size through a
real mount, and writes `docs/Performance.md`. Most of it reads well — around 700 MiB/s sequential
write and 2-3 GiB/s sequential read, small-file reads scaling roughly five-fold across twenty
threads. One row does not.

**Cached random 4 KiB reads go BACKWARDS with concurrency**: about 69,000 IOPS on one thread and
about 53,000 across twenty, on a twenty-CPU host. Reads that all hit RAM should scale close to
linearly, so this is threads queueing on something rather than a hardware limit.

Ruled out: the per-file read-ahead map, which was a `Dictionary` behind `lock (File.ReadAhead)`
taken on EVERY read and shared by every handle open on that file. It is now a
`ConcurrentDictionary` — worth doing regardless, and it moved nothing measurable, so it was not the
bottleneck. The remaining suspect is `HandleTable`: every read takes the table's single global lock
to resolve the path, then the file's `ReaderWriterLockSlim`. At tens of thousands of operations a
second across twenty threads, one global monitor per read is enough to explain this. Not yet
isolated with a profiler, which is what the next pass should do rather than guessing again.

The benchmark itself was wrong first time round and is worth remembering: it opened a `FileStream`
per operation, so it measured open+read+close and reported it as read IOPS — the read path looked
four times slower than it is (15,000 rather than 69,000). Each worker now takes its handle before
the clock starts.


### Why writes look slow, and how much of it is deliberate

The write rows in `docs/Performance.md` were all within a few percent of each other — around
700 MiB/s whether the pool's cache was 3 GiB or 32 MiB. That is the tell: **cache size makes no
difference to a write because a write never touches the cache.** By default an acknowledgement
means the bytes are on a disk (SAFE-NOLOSS), so the rate is the device's, minus the engine.

Measured, so the trade-off is priced rather than argued:

| | Sequential write, 1.5 GiB |
| --- | ---: |
| Raw device, buffered, one fsync at the end | 1,920 MiB/s |
| Raw device, WRITE_THROUGH (what a member handle uses) | 1,930 MiB/s |
| Pool, default (ack means on disk) | ~660 MiB/s |
| Pool, `write.policy: performance` + `acceptVolatileAck` | ~1,100 MiB/s |

Three things fall out of that:

1. **WRITE_THROUGH is free on this device** — 1,930 against 1,920 buffered. It is not the cost, and
   an NVMe that ignores the distinction is the common case now.
2. **The RAM-ack path is worth roughly 1.4–1.7x** and already exists as an explicit per-folder
   opt-in. Nothing needs building for a user who wants it; it needs documenting so they know the
   choice is theirs.
3. **A gap to the device remains even with the ack from RAM** — 1,100 against 1,920. That is engine
   overhead on the write path, and it is worth chasing.

Ruled out while looking, so the next pass does not repeat it:
- **Not transfer chunking.** WinFsp delivers the application's writes whole — instrumented and
  counted: 64 callbacks averaging exactly 8 MiB for a 512 MiB write, not the 64 KiB fragments that
  would explain this.
- **Not WRITE_THROUGH**, per the table above.

The strongest remaining suspect is `PoolFileSystem.Write`, which begins `var bytes = data.ToArray()`
— a full copy of every write before anything else happens, so an 8 MiB write allocates 8 MiB on the
large object heap and a 512 MiB transfer produces 512 MiB of garbage. The copy exists so the write
buffer can take ownership when copies are owed; when nothing is owed it is pure waste. Making it
conditional touches the ack-quorum and journalling path, which is the most safety-critical code in
the product, so it is written down rather than attempted in passing.

### The storages behind a tier were taking turns instead of working together

`CFG.io.queueDepthPerVolume` was decoration. `ConfigValidation` checked the numbers in it and
nothing ever read them, so §6.4's "HDD default 2, SSD/landing 8+" described a setting that did
not exist. `io.elevatorOrdering` is in the same state and still is — it is a PRD "Could".

What that cost is bigger than a missing knob. A read that spanned several blocks loaded them ONE
AT A TIME unless the file was duplicated AND the read was over `mirrorReadSplitThreshold`
(8 MiB) — so the ordinary case ran a device at queue depth one.

**And on Linux that threshold can never be met.** Measured through a real mount with libfuse's own
tracing (`DBMOUNT_FUSE_DEBUG=1`), reading a 64 MiB file with an 8 MiB application buffer: 512 READ
requests, every one of them exactly 131,072 bytes. The kernel splits a read into 128 KiB requests
and the adapter sets no `max_read`/`max_pages` to change that — writes arrive whole at 1 MiB
(`max_write=0x100000`), which is why the write path looked fine and the read path did not. So
`count >= 8 MiB` was false for every read FUSE has ever delivered, and mirror read splitting has
never engaged on that target at all. It was not tuned badly; it was unreachable.

Which also says where the win on Linux actually comes from. A 128 KiB request touches at most two
1 MiB blocks, so fanning out WITHIN one request buys little there — the lever is the read-ahead
window, the one thing on the read path whose entire job is to keep the device busy, and it fanned
out to `copies.Count`, which on an unduplicated file is one. That is the queue depth of one, and
it is what the measurement below moves.

Now: any multi-block request loads its missing blocks concurrently, `VolumeQueues` sizes the
fan-out from the summed queue depth of the DISTINCT devices the copies sit on (two members on one
spindle are one queue, not two), and each device has a cap it is admitted through. Defaults are
asymmetric on purpose — 2 for rotating media, 4 for a member whose operations park the caller for
a network round trip, and a host-scaled depth for anything solid or unrecognised, because a cap
that binds on a fast local device costs throughput to protect against nothing. Media is
classified from sysfs on Linux; every other platform answers Unknown, so `hdd`/`ssd` keys do not
bind there and a member name, a role or `default` does. Guessing at the Windows seek-penalty
ioctl untested would mis-tune every pool on that platform silently.

**Measured, on this host** (16 CPUs, `/tmp` on tmpfs, so this is the RAM path rather than an SSD):
cold sequential read of 512 MiB through a real mount, same pool, same cache, only the queue depth
differing. The benchmark carries that control row permanently
(`Scatter_OverlappedIoAcrossStorages`), because it needs no second device to mean something —
queue depth 1 IS what the engine used to do.

| | 512 MiB cold sequential read |
| --- | ---: |
| queue depth 1, before single-flight (the old engine) | 1,313 MiB/s |
| queue depth 1, with single-flight | 1,379 / 1,422 MiB/s |
| overlapped | 1,601 / 1,631 / 1,663 MiB/s |
| overlapped, two copies on one device | 1,551 / 1,596 MiB/s |

Read it as: overlapping is worth about **1.15x** over the serial path as it now stands, and about
**1.22x** against the engine as it was. Repeat samples are given rather than one number because
this host drifts ±7% run to run and a single figure would be a claim the measurement does not
support. Single-flight is what lifted the serial control from 1,313 to ~1,400 and did nothing
measurable to the overlapped figure — which fits: at queue depth 1 the reader constantly outran
its read-ahead and re-fetched the block it was already loading, and with a wide fan-out the
read-ahead is far enough ahead that there was little duplication left to remove.

The `2 copies` row sits slightly BELOW the single-copy overlapped number, which is the honest
result when both copies share one device: the split costs a few percent and can only pay for
itself on hardware that actually has two.

**Whole-file transfers were single-buffered.** `WholeFilePublisher.CopyCounted` alternated
read-chunk / write-chunk on one thread, and every caller that matters moves bytes BETWEEN TWO
STORAGES — a landing-zone drain to capacity, a duplication heal, a media move. So the two devices
took turns and each sat idle for the other's half of every chunk. It is double-buffered now:
chunk N+1 is read while chunk N is written, so the transfer costs max(read, write) rather than
their sum. Two details that a review should not have to rediscover — an outstanding read OWNS a
rented buffer, so the copy waits for it before returning either array to the pool even when it is
unwinding from an exception; and the read-ahead goes through a fill-completely helper, because
one `Read` may return short at any point and "short chunk means exhausted" is only true after
that. Without the helper the overlap would have turned every dribbling network read into its own
thread, and treating the first short read as EOF would have truncated files silently.

**A dying storage was being asked first, over and over.** Failover tried copies in a fixed order,
so a member whose reads error was tried FIRST for every block of every read, with a healthy copy
beside it. On real hardware each of those is a driver timeout measured in seconds. A member that
throws is now parked at the back of the readiness order for five seconds — never removed, because
one bad sector is not a dead disk and failover has to stay a fallback rather than a fork in the
code. The device cap has a matching escape: admission waits at most five seconds and then goes
anyway, so a member that hangs while holding a permit cannot turn a tuning knob into a hang.

**The same block was being fetched twice, and the read-ahead could not pipeline.** Two callers
who wanted the same uncached block issued two device reads — so a reader that outran its own
read-ahead re-fetched the very block the prefetch chain had in flight. And there was exactly ONE
read-ahead chain per path, so the chain that would fetch window N+1 was not allowed to start
until the chain fetching window N had finished: the moment the application caught up with the
frontier it waited at device latency instead of at cache latency, which is precisely the stall a
read-ahead exists to prevent.

`_LoadBlock` now single-flights. The interesting part is what makes sharing SAFE, because the
naive version is a stale read:

- **Only an identical question is shared.** The `expectedLength` a caller passes is what decides
  whether a short block means "this copy is behind, fail over" or "this is the end of the file".
  Two callers with different expectations are asking different things, so they do not share.
  Getting that direction wrong hands a lagging copy's short block to a caller that asked for it
  to be verified — the class of bug recorded twice above.
- **A failure is never inherited.** The leader may simply have been unlucky in which copy it
  tried; a caller whose own attempt might succeed has to make it.
- **A background read-ahead runs without the path lease**, so its bytes may predate a write that
  has since landed. A lease-holding reader may take them only if the pool's invalidation epoch is
  still what the leader captured before reading — the epoch moves in one direction, so equality
  proves the whole interval was quiet. That epoch is POOL-wide, so an unrelated write costs an
  adoption that was in principle keepable. Conservative in that direction is free; wrong in the
  other is a stale read.

With duplicates gone, a second chain is safe, and there are now two. The read-ahead also skips
blocks that are already IN FLIGHT rather than queueing behind them — a chain's job is the bytes
nobody is fetching yet.

Pinned by two guards that need no clock: eight threads racing for one uncached block must cost
the storage exactly ONE read, and a 48-block file read end to end must cost exactly 48 — anything
above that is the reader and the read-ahead, or two chains, fetching the same block twice.
Mutation-checked: disabling single-flight fails both.

Honest limit: the SECOND chain's benefit is latency, and latency is the one thing those two
guards cannot see. It is reasoned above and visible end to end; there is no unit test that fails
if the chain limit goes back to one.

### Three crash scenarios were betting on the host being slow

`Crash_GivenStagedWritesWereInterrupted_...`, `Crash_GivenARenameWasInFlight_...` and
`Crash_GivenAnOverwriteWasInFlight_...` all needed the power to go off WHILE something was
happening, and each arranged that by racing a fixed amount of work against a fixed sleep. That is
a bet on the machine. On a 16-CPU host with `/tmp` on tmpfs the work finishes first and the
scenario silently becomes "crash after everything completed" — which is why two of the three
tripped their own `or this tests nothing` guard, one per full run. They were right to refuse to
report green, and raising the workload would only move the goalposts until the next machine.
Making the I/O path faster makes it worse, so this had to be fixed alongside.

The two that failed now run an UNBOUNDED loop: there is no amount of work to finish, so being
mid-flight when the power is cut is structural rather than lucky, and the only thing left to
establish is that the workload got far enough to be worth interrupting — an observation, not a
race. The rename scenario gained something from it: instead of one sweep through twelve files it
now renames back and FORTH without end, reading each file's current name off the disk so an
interrupted sweep never leaves it moving a name that is not there. The third was passing with a
comfortable margin and kept its structure — a single overwrite through ONE held-open handle,
which is the case the staged-write lifecycle has to survive and which closing per chunk would
make easier — but it now cuts the power on OBSERVED progress instead of after a fixed wait.

None of this was a product defect and none of it was a data-loss finding: every assertion about
surviving bytes passed on every run, before and after.

**A fourth test was racing the host in the same way, and it took longer to see because it looked
like a product bug.** `LongOperation_GivenTheDriverInstallEndpoint_...` starts an un-stoppable job
and then asserted that cancelling it comes back `ok: false`. `JobRegistry.Cancel` checks
`IsFinished` before `Cancellable`, so on a machine where the prerequisite is ALREADY satisfied the
installer finishes in microseconds and the answer is the equally honest `already finished`
(`ok: true`). The daemon is right and the assertion was too specific: which of the two honest
answers arrives depends on the host, and the invariant that actually matters is that an
un-stoppable job never claims to be STOPPING. That is what it asserts now.

**And the four `WebUi` failures were the environment, verified rather than assumed.** Playwright
1.49 wants Chromium build 1148; this machine's cache held build 1234 from a newer Playwright, so
`OneTimeSetUp` refused — correctly, because `DBE2E_REQUIRE_DRIVER=1` says a missing browser is a
failure and not a skip. Installing the pinned build turned all four green. Worth recording because
"it is the environment" is exactly the claim that should not be made without checking.

### Linux end-to-end was RED in CI, I did not look, and two of the three were platform asymmetries

Every "suite green" in this file and in my reporting came from a local Windows run. The Linux
end-to-end job has been failing since at least 13 August across many commits, and nothing here said
so. Local runs are not CI, and a claim of green that was never checked against the second target is
worth nothing on that target. Three distinct failures, all reproduced on `main` and none caused by
the commit they happened to land on:

1. **A torn read through FUSE — and it was the TEST that was wrong.**
   `DriverEndToEndTests.Concurrency_GivenManyReadersAndWriters...` required every read to come back
   either empty or exactly 32768 bytes, and reported short reads such as 4096. That was recorded
   here as the most serious of the three, on the reasoning that a reader observing a
   partially-written file is the corruption the concurrency design exists to prevent.

   It is not a pool defect. `File.WriteAllBytes` truncates and then writes, and a whole-file read is
   not atomic against that on ANY filesystem. Measured rather than argued: the same four writers and
   four readers against a plain tmpfs directory produce 1-6 short reads per run, and against btrfs
   2-4 — the pool is doing exactly what the bare kernel does. Content is not promised either; a
   plain filesystem occasionally returns a full-length BLEND of two versions, because the reader
   took some pages before the overwrite and some after. The scenario "passed" on Windows only
   because share violations there turn the same race into an IOException the test already tolerates,
   so the platform difference was never evidence of a platform defect.

   The scenario now asserts what a pool genuinely owes and a filesystem cannot excuse: no read may
   be longer than any version, every file settles on ONE WHOLE version once the contention stops,
   and — the part only a pool can get wrong — **no read may contain a block belonging to a
   DIFFERENT FILE**. Payloads are random per file and per round, so a 16-byte fingerprint identifies
   its owner beyond coincidence, and a cache keyed so two paths collide, a placement resolved to the
   wrong member or a handle reused across names would all fail it. That is strictly stronger than
   the length check it replaces, and it passes nine runs in a row where the old one failed two in
   nine.

   One caution for whoever writes the next oracle of this kind: the first version of it fired
   immediately, and the cause was the fixture, not the engine. All four files were seeded with the
   same initial payload, so their blocks were indistinguishable and each file's own data was
   reported as another's. Seeds are distinct per file now.
2. **Two names differing only in case cannot both exist. FIXED.** The engine compared paths with
   `OrdinalIgnoreCase` everywhere, on both platforms. On POSIX that is not cosmetic: the handle
   table, the page and metadata caches, the write buffer, the staging map, the trash, the checksum
   database, the shadow namespace and the directory merge all folded the two names onto one entry,
   so copying a case-sensitive tree into a pool lost whichever file landed second — silently, with
   one name's bytes serving the other's reads. `LocalVolumeIO`'s pooled handles collided the same
   way, which would have served one file's content for the other even had the rest been correct.

   `PoolPaths.PathComparison`/`PathComparer` is the single authority now — insensitive on Windows,
   sensitive elsewhere — and every path-keyed structure uses it. Deliberately NOT changed: the
   comparisons that key on a physical volume id, a member display name, a config key or a fixed
   marker name, none of which are pool paths.

   **The fix has a hazard of its own, and closing it is the more interesting half.** The platform is
   an approximation of what the STORAGE does: an NTFS volume or an SMB share mounted under Linux is
   case-insensitive on a case-sensitive host. There, "delete the old target, then rename onto it"
   deletes the file being renamed — the engine would have destroyed data in a configuration where
   the old code was safe. So `IVolumeIO.IsCaseSensitive` asks the member instead of assuming, and
   the rename path skips the overwrite-delete when the two names differ only in case and that member
   does not tell them apart. `LocalVolumeIO` answers it by probing, and without writing anything:
   every member carries `.drivebenderutility/member.json`, so asking whether the SHOUTED spelling of
   that same path also exists settles it. This is what the engine unit suite caught the moment the
   comparison changed, on a fake whose namespace is case-insensitive.
3. **The mount detached during eject/restore. FIXED, and it was worse than "the scenario fails".**
   Pulling a member out from under a live mount killed the FUSE session: everything the user had
   open on the pool failed with `Transport endpoint is not connected`, from ONE disk going away,
   which is the exact opposite of what a redundant pool is for. Two causes, both asymmetries where
   the Windows path had been hardened and the Linux one had not:

   - `LocalVolumeIO.BytesFree`/`BytesTotal` raise `DriveNotFoundException` on Linux when the member
     path is gone. The Windows branch of the same method raises the engine's own `PoolFsException`,
     which every catch in the engine is written against; the raw System.IO exception sailed through
     all of them. They now answer 0 — the documented FR-STAT convention for "capacity unknown" —
     because these are PROPERTIES, read from LINQ pipelines, from placement on the write path and
     from a background timer, and a member disappearing is the ordinary event this product exists to
     survive rather than something that should arrive as an exception from a size.
   - The FUSE host's pump timer had no try/catch, and an unhandled exception in a `Timer` callback
     kills the process. The WinFsp host has carried exactly that guard, with exactly that comment,
     for some time. The same timer was also PERIODIC, so a slow tick overlapped the next one and ran
     `Pump()`/metrics/reload concurrently against the engine; it is single-shot and re-armed at the
     end of each tick now, again matching Windows.

None of the three is still open. What made all three hard to see is worth keeping: two of them were
platform asymmetries where one host had been fixed and the other silently had not, and the third was
a test asserting a guarantee no filesystem makes.

### Small-file creation: measured against the floor, and a target of mine was wrong

Small-file creation was the worst number in the product, and it is now roughly 70% faster. More
usefully, it has been measured against what the operating system can actually do, which turns out
to matter more than any of the guesses about it.

The pool's durability shape for one small file is: stage to a temp, flush, atomically rename, once
per copy. Doing exactly that from plain .NET, with nothing else in the way:

| | 1 thread | 20 threads |
| --- | ---: | ---: |
| Raw NTFS, 2 copies, fsync (the floor) | 322 files/s | 480 files/s |
| The engine, no driver | 162 | 322 |
| Through the mount | 113 | 148 |

Two things follow, and the second corrects a target I set:

1. **The engine sits at about half the floor single-threaded and two thirds of it at twenty
   threads.** The remaining gap is journal barriers: a create still costs two, and each is fsynced
   to every member, so a two-member pool pays four journal flushes against the floor's two data
   flushes.
2. **The 20-thread target of >1,000 files/s was not achievable and should not have been written.**
   The operating system itself only reaches 480 with this durability shape — fsync and
   single-directory metadata serialise inside NTFS, which is why the raw floor scales just 1.5x
   across twenty threads. Any figure above that requires giving up either the fsync or the
   per-member copy, which is a durability decision and not a tuning one.

**Ruled out along the way, with numbers, so nobody re-walks it:** `HandleTable`'s single global
lock is NOT the limiter. It sustains ~1.8M acquisitions/s under twenty-way contention and the read
workload needs about 110k/s — and critically, twenty threads on DIFFERENT paths scale no better
than twenty on the same path, which rules out the per-file lock too. This was the leading suspect in
the plan and the profile refuted it before any code was changed.

The one clear remaining lever is the journal mirror: every barrier is fsynced to EVERY member, so
halving that to a durable quorum with the mirror written behind it would remove two of the five
flushes a create pays. That is a genuine durability trade — a journal on one disk is a journal that
one disk loss destroys — so it belongs to whoever owns the risk, not to a performance pass.

## Still open

An earlier revision of this file claimed "nothing known" here. That was wrong — it dropped the two
UI items below. Both are now closed, and they are kept rather than deleted because what they cost
is the point: a capability gap that is merely WRITTEN DOWN reads as a decision, and these two sat
here through several passes looking like ones.

1. **Jobs relayed into a MOUNTED pool's own process could not be cancelled. FIXED.** Pool work
   never runs inside the manager — the manager is a reload-safe UI shell and a mounted pool owns
   its engine — so a scan or a restore on a mounted pool is FILED as a request and executed over
   there. That relay was one-way: once filed, the manager had nothing more to say. Such jobs
   reported `cancellable: false`, which was honest and useless, because it meant a deep scrub of a
   mounted pool could not be stopped for however many hours it took.

   `MountRegistry` now carries the other two directions in the same channel directory and with the
   same file-drop shape as the request: a stop marker going in, a progress file coming back. The
   pool's process polls the stop marker BETWEEN ITEMS via `OperationContext`, never inside one — a
   scrub abandoned mid-repair, or a restore abandoned mid-copy, is precisely the torn state those
   operations exist to fix. A stopped pass answers `cancelled`, which is a distinct outcome from a
   failure because everything it managed first is real work that was kept: the checksum baseline is
   persisted in a `finally`, so cancelling a scrub means "stop here", not "throw away what you
   learned on the way".
2. **Progress was per-job, not per-item. FIXED.** The only thing a job could report was its
   worker's most recent line of output. That cannot be drawn as a bar, cannot say whether an
   hours-long pass is a tenth or nine tenths through, and cannot tell "still working" from "wedged
   on one enormous file" — which is most of what the person watching it wants to know.

   `IntegrityService` and `MediaLifecycle.RestorePool` now materialise their work list up front and
   report `(completed, total, item)`; `HealthService` threads it through; the job ticket and the SSE
   frame carry the counts; the modal draws a bar. `Total` stays 0 while the pass is still finding
   out how much there is, reported honestly rather than guessed — a denominator that grew as the run
   went would make the bar fill and reset, which is a lie about progress rather than a report of it,
   and for the same reason the bar only appears once the total is known. Publishing is rate-limited
   to one channel write a second, because a scrub steps once per file and a pool has millions.

   **Both verified end to end through the shipped binary**, not only in unit tests: a deep scan of a
   mounted 21,300-file pool reported `cancellable: true` (it reported `false` before), streamed
   `20,977 / 21,300 — big/g322.bin` to the HTTP API the browser polls, accepted a cancel with
   `"cancelling"`, ended as `{"ok": false, "error": "cancelled"}`, and a second scan afterwards
   completed normally with no issues — so stopping one left the pool and its baseline intact.
3. **A file replaced by rename keeps serving its OLD content to new readers — Windows.** After
   `File.Move(source, target, overwrite: true)` succeeds, a reader that opens the target path
   afresh still gets the pre-replacement bytes. Measured through a real WinFsp mount: six
   replacements landed and the file settled on version 40, while all 3,200 reads taken during the
   run returned version 1 — so the replacements were real and every reader missed all of them.
   Atomic replace-by-rename is the pattern careful software uses precisely to publish a new
   version safely, so this makes the safe pattern the broken one. Pinned by
   `SharedAccessEndToEndTests.SharedFile_GivenWritersReplacingItByRename_...`, `[Ignore]`d with
   this reason rather than weakened.

   **Ruled out: IndexNumber.** `WinFspAdapter._Fill` never sets `FspFileInfo.IndexNumber`, so
   every file reports file id 0, and Windows associates a file's cached data section with its
   IndexNumber — a good story for why the section survives a replacing rename. It was implemented
   (a real per-file identity from path plus creation time, which stays constant across appends and
   changes when the name comes to hold a different file) and it does NOT fix this. Reverted rather
   than carried, because it costs a hash on every `GetFileInfo`, which is a hot path, and bought
   nothing measurable. Setting it may still be worth doing for its own sake; it is not the lever
   here.

   **New evidence, and it narrows things a lot.** Re-measured this pass: 16 replacements landed,
   all 3,200 reads during the run returned version 1 — and the read taken AFTER the workers stopped
   returned version 60. So the bytes on disk are correct and the invalidation is not permanently
   broken; the staleness lasts exactly as long as readers keep the name open. Whatever serves those
   reads is pinned by an open handle and outlives the rename underneath it. The engine's own
   **Also ruled out, so the next pass need not re-walk them:**
   - The engine's `Rename` does invalidate both endpoints, and `_Invalidate` clears placement and
     the path's cache entry under the same name it caches them — no mismatch there.
   - The pooled physical handles in `LocalVolumeIO.HandlePool` are not it. `AtomicReplace`
     invalidates before AND after the swap, and `_Retire` removes the key from the dictionary, so a
     handle rented before the replace is closed when its borrower returns it rather than re-pooled
     — a later reader cannot be served the pre-replace file out of the pool.

   What is left is whatever a READER holds across the replace, since the staleness ends the moment
   the readers stop. `FileState.ReadAhead` is per-handle and survives a rename — `RenamePath`
   repoints the state rather than retiring it — which makes the read-ahead buffers the most
   promising remaining candidate.

   It also passes when run ALONE and fails in the full suite, so it is timing-sensitive; a single
   green run of this scenario means nothing without the whole suite behind it.
4. **`_RenameFolder` holds leases on the two folder paths but on NO CHILD FILE while moving them.**
   It flushes dirty children and publishes staged ones first, but takes no lease on any of them, so
   a write can land between that flush and the member-level `RenameFolder` and address a path whose
   physical file has since moved. Not reproduced end to end; the stress suite races file renames
   only, which is why it would not be caught. Fixing it needs a lock-ordering story for an unbounded
   set of children, which is why it is written down rather than attempted in passing.

   **Half of this entry was wrong and is withdrawn.** It also claimed `HandleTable.RenameSubtree`
   repeats the defect fixed in `4ad2094` by re-keying children over any state already there. It does
   re-key that way — and so does `RenamePath`, the method that fix landed in. Displacing an entry was
   never the defect; the defect was the CLOSE path unkeying an entry that had come to belong to
   somebody else, and `4ad2094` fixed that centrally by guarding both removal sites with
   `ReferenceEquals(current, file)`. `RenameSubtree` therefore has the shape of the FIXED code, not
   of the bug. Checked against the commit rather than inferred from the shape a second time.

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

## Known limits, deliberately

- **A remote member whose provider can neither range-read nor stream cannot hold a file over ~2 GiB.**
  Such an object has to pass through a `byte[]`, and .NET caps one just above 2 GiB. The engine now
  says exactly that — naming the file, its size and the missing capability — instead of surfacing an
  OutOfMemoryException from inside a provider SDK. Providers that stream are no longer capped: the
  whole-file read path uses the stream when ranges are unavailable, and the interface's ranged-read
  default skips forward through a stream rather than slicing an array. Local members, and any
  provider with range or streaming support, have no such limit.

## Standing guards

These now fail the build rather than needing to be re-found:

- `ConcurrencyStressTests` — many readers against many writers on the same paths, opposing renames,
  create/delete churn; a self-describing content scheme detects a torn read, per-reader monotonicity
  detects a vanished write, a watchdog names any stuck worker and the operation it was inside, and
  budgets bound retained heap, allocation, idle pump work and CPU-per-wall-clock.
- `CrashConsistencyTests` — write, delete, rename and create each interrupted at **every** volume
  operation they perform, with a guard that fails if a case sits past the operation's real length
  and therefore tests nothing.
- `LargeFileEndToEndTests` — a 2 GiB + 64 MiB file through a real mount: the length is reported in
  full, reads land correctly on both sides of `int.MaxValue` and 2 GiB, an append extends the true
  end rather than wrapping to the start, a positional write past 2 GiB touches only its own region,
  and the file streams end to end. The fixture caps the pool's cache at 256 MiB on purpose: the
  default global cache is 4 GiB, so a 2 GiB file fits in it entirely and the mount's memory would
  sit near the file size for a perfectly good reason — with a small cache, memory has to track the
  BUDGET, which is the property SAFE-BIGFILE actually claims. Measured 469 MiB peak for a 2112 MiB
  file at 2333 MiB/s. It self-skips, with the reason, when the machine lacks ~6 GiB free.
- `RentedBufferSafetyTests` — every pooled-buffer site run against a DELIBERATELY DIRTIED
  `ArrayPool`, so each rented array arrives full of a poison byte, and read through a stream that
  dribbles a few bytes per call. A clean pool hands out zeroed arrays and would pass all three of
  the mistakes this guards: trusting `buffer.Length` as "how much data there is", trusting one
  `Read` to fill the buffer, and letting a rented array escape after return. Mutation-checked —
  writing one byte too many from the buffer fails three of the four. A fifth guards the mistake the
  poison cannot catch, because it produces CORRECT output: a rent returns an array at least as big
  as asked for and usually bigger, so reading its `Length` transfers a size chosen by the pool's
  bucket rather than by the caller. The test asserts its own premise first — that the pool really
  does round up — so it cannot pass vacuously.
- `EnginePerformanceTests` — allocation budgets for folder-config resolution, block routing, the
  activity-feed drop path, cached reads and write staging.
- `ScatterIoTests`, `DoubleBufferedCopyTests`, `VolumeQueueTests` — the overlap itself, which no
  assertion about returned bytes can see: a serial engine gives the same answer as a fan-out one,
  and an engine that retries a dead disk once per block gives the right answer too, just far too
  late. The engine is driven through a member that COUNTS what happens to it — peak concurrent
  reads, and read ATTEMPTS including failures. Each claim is pinned from both sides, so the probe
  cannot pass vacuously: a multi-block read must exceed one in flight, the same read with
  `queueDepthPerVolume: 1` must never exceed one, and a read inside a single block must cost
  exactly one. Two more count DUPLICATED work, which is the other half of using a device well:
  eight threads racing for one uncached block cost the storage exactly one read, and a 48-block
  file read end to end costs exactly 48. Mutation-checked — forcing the fan-out back to one and
  dropping the failure note fails three of them; disabling single-flight fails the other two.
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
