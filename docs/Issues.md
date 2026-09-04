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

### A file that lost its disk was reported as EXISTING AND EMPTY, and the read succeeded

Found by putting pool members on genuinely different physical devices for the first time — a fast
internal disk in front of a 119 MiB SD card — and then pulling one of them at an awkward moment.
The scenario that caught it interrupts a landing-zone drain, but the defect has nothing to do with
tiering and everything to do with what `retain-metadata` remembers.

**The defect.** `PoolFileSystem.Create` records the new path in the shadow namespace with a length
of ZERO, which is true at that instant. The only thing that ever refreshed it was a stat that MISSED
the metadata cache (`_GetAttributesLocked`), because the cache-hit path returns before the record is
touched. So a file that is written, closed and never stat'd again keeps a remembered length of zero
for its whole life. That number is invisible while any copy is reachable — and it is exactly the one
`retain-metadata` answers with when none is (§10 SAFE-DEGRADE). Lose the disk under such a file and
the pool reports it as existing and empty, so `File.ReadAllBytes` returns NO BYTES AND SUCCEEDS.

That is the worst shape this product can fail in. An error tells the application something is wrong;
a short success tells it the file is simply that small, and the emptiness gets written onward.

Observed through a real mount as the sequence `FileNotFoundException, FileNotFoundException,
FileNotFoundException, 0, 8388608` while a disk came back under an 8 MiB file — the zero sitting
between the honest refusals and the correct answer.

**Why the existing cover did not catch it.** `RetainMetadata_GivenMemberHoldingOnlyCopyLost_...`
lists the directory before pulling the member, under the comment `// warm the shadow namespace`.
That warm-up is load-bearing: it forces the uncached stat that records the real length, and without
it the same assertion reads 0 instead of 3. The test was documenting the workaround rather than the
behaviour.

**Fixed** by refreshing the remembered size when a WRITE handle closes (`Close` and
`MarkApplicationClosed`, so the Windows deferred-close path is covered too). Once per write handle
rather than once per write is what keeps it off the hot path — a close already flushes and may
publish a staged file, so one stat beside that is noise. Pinned from both sides: a unit guard that
writes a file, never stats it, pulls its only member and refuses a length of zero; and an end-to-end
guard that fails if any read SUCCEEDS with fewer bytes than the file holds while a disk returns.

### `dbmount unmount` refused a pool that was plainly mounted, and left it mounted

Reproduced 8 times out of 8, then 0 out of 6 after the fix.

Mount a pool and unmount it straight away, and the unmount answers `No mounted pool matches '<name>'`,
exits 2, and the pool STAYS MOUNTED with its process still serving it. `dbmount status` does not
list it either. Wait a second and both work.

**The cause is a platform asymmetry, the third of that shape in this file.** Both verbs look the pool
up in the cross-process mount registry. The Windows host registers its entry INLINE, the moment its
`Mount()` call returns. The Linux host cannot — libfuse offers no "you are mounted now" callback — so
it registers from the background pump the first time it sees the mount in `/proc/mounts`, and that
pump first fired one second after start. Between "the filesystem works" and "the tooling knows it
exists" there was therefore a window of up to a second, and mount-then-unmount is the shortest script
anyone writes. The pump now polls at 50 ms until it has registered and reverts to one second
afterwards, so the window is a twentieth of what it was and no longer straddles a plausible command.

**What it cost, which is the part worth keeping.** The end-to-end harness hit this on nearly every
pool it built: its teardown asked for a clean unmount, DISCARDED the failure, then waited out the
full 60-second timeout and killed the process. So the defect presented as "some tests are slow" and
nothing else — the same 17 scenarios take **1m0s where they took 9m53s**, and the whole battery
dropped from about 46 minutes to under 20. Two lessons, both cheap:

- The harness now reports a teardown that drags AND the exit code of the unmount it asked for. A
  discarded error is how a product defect hides inside a test harness for a whole session.
- Every killed mount process leaves its `.mountlock` (which relies on `DeleteOnClose`) and its
  `.metrics.json` behind, because nothing gets to clean up. This machine had accumulated **874** such
  files. `MountRegistry.List` globs `*.json` in that directory, which also matches every
  `<pool>.metrics.json` snapshot, so each one was read off disk and thrown at a deserializer that
  could only fail — several hundred file reads and as many exceptions per `status`, and per each of
  the hundred polls one `unmount` makes. It now only considers files named for a pool id.

### Per-kind I/O limits, so a disk can be left usable for everything else

`maxThroughput` was one number covering every operation, which is the right default and a blunt
instrument. The three kinds of work a pool does compete for a disk on completely different terms: a
READ is what an application is blocked on, a WRITE is what it is blocked on to be safe, and
BACKGROUND — a landing-zone drain, a duplication heal, a media exchange — moves far more data than
either and nobody is waiting for it. Holding the last one down while leaving the first two alone is
most of what "leave some of this disk for something else" means, and it could not be expressed.

Three shapes now, and the advanced dialog offers exactly those: **none**; **simple**, one rate for
everything including the pool's own copying; or **advanced**, a separate rate for reads, writes and
healing/exchanging. Operations per second sits alongside them, because a seek-bound disk runs out of
those before it runs out of bandwidth.

**Time limits, in the same three shapes**, and they mean something narrower than the word usually
does: a limit is a target, and the time limit is the promise that honouring it costs no more than
this. Past it the operation proceeds having paid what it could. That is deliberate — it means a rate
typed in wrong can slow a pool down and can never wedge it, and an operator who mistypes a number has
a way back that does not involve unmounting. It bounds the QUEUEING only; device I/O already in
flight cannot be cancelled on a synchronous handle, and claiming otherwise would be a guarantee the
engine cannot keep.

Two details worth keeping:

- **Each kind gets its own token bucket, and every bucket refills whether or not it is being spent.**
  A shared bucket would make three rates meaningless the moment more than one was set — a heal at its
  own generous rate would spend the credit a read was about to need. And refilling only the bucket in
  use would make a kind that goes quiet pay for its own idleness when it came back.
- **Older manifests keep meaning what they meant.** `maxIops`/`maxThroughput` read as the simple
  shape, the detailed block is written beside them rather than instead of them, and the two are kept
  in step so nothing that only knows the old shape can read a manifest and be misled by it.

Settable from the dashboard per member, applied to a RUNNING pool — which is the situation the whole
setting exists for, since the disk you need to ease off is rarely one you can take offline.

### Open: a landing zone absorbs nothing on Windows — the burst runs at the CAPACITY tier's pace

Found by giving the tiering claim a control instead of an absolute bar: the same burst written to a
pool that is only the slow tier, on the same machine, moments earlier. Both sides then pay the same
journal, staging and fsync costs, so what is left is the tier. Measured on the CI runners:

| landing zone over capacity | through the landing zone | straight to capacity |
| --- | ---: | ---: |
| RAM over SD card — Linux | 115 MiB/s | 30 MiB/s |
| RAM over cloud — Linux | 61 MiB/s | 11 MiB/s |
| RAM over SD card — **Windows** | **22.5 MiB/s** | 23.4 MiB/s |
| RAM over cloud — **Windows** | **9.8 MiB/s** | 10.4 MiB/s |
| SSD over SD card — **Windows** | **22.5 MiB/s** | 23.3 MiB/s |
| HDD over cloud — **Windows** | **3.8 MiB/s** | 3.6 MiB/s |

On Linux the fast tier is doing exactly what it is for. On Windows it is worth nothing: every pairing
lands within a few percent of writing straight to the slow disk, which is the one outcome a landing
zone exists to prevent. The bursts are small enough that the drainer has barely started, so this is
the INTAKE being paced by the capacity tier rather than a drain competing with it.

A timing-free companion scenario now runs on BOTH platforms and reports where the burst physically
landed. It asserts only what needs no clock — that new data goes to the landing zone — so whichever
way it falls on Windows it narrows the search: if the burst is not on the fast tier the fault is in
placement, and if it is, placement is fine and whatever paces the write sits downstream of it.

Not diagnosed further than that, because it needs a Windows machine to instrument and this pass had none —
what is established is that the effect is real, reproducible across four pairings and two capacity
speeds, and specific to that platform. Note that
`TieringEndToEndTests.Tiering_GivenAFileIsWritten_ThenItLandsOnTheFastTierAndDrainsToCapacity` passes
on Windows, so files DO land on the fast tier there; whatever paces the write is downstream of
placement. The scenario is held back on Windows with the measurements in its skip reason rather than
weakened, so it keeps failing honestly on Linux if the gain ever disappears there too.

### The pool's own bulk copies ignored the rate limit set on the disk they were writing to

Found by trying to build a race against the drainer and failing to: a 24 MiB file copied onto a
member held to 1 MiB/s finished in under six seconds.

`maxThroughput` is applied in `VolumeQueues.Enter`, which the engine's BLOCK paths call.
`WholeFilePublisher` streams straight into the member and never went near it — so the drain, the
heal, the media move and every other whole-file transfer bypassed the limit completely. That is
precisely backwards: the setting exists because "a pool is rarely the only thing using a disk", and
the pool's own background copying is the largest thing it ever does to one. An operator who told the
pool to go easy on a member still had it saturated by drains and heals.

`CopyCounted` now takes an admission hook and the drain and heal paths pass it, so a bulk chunk waits
out the destination's limit exactly as a block write does. Pinned from both sides by the scenarios
below, which could not exist before it: they need the copy to still be RUNNING when the foreground
operation lands, and it never was.

Still unwired, and deliberately: `MediaLifecycle` (scatter, replace), `PoolRecovery`, `PoolTrash` and
the integrity quarantine build their own copies without a queue to hand. Those are operator-initiated
one-off operations rather than continuous background load, so they matter less and threading the
queue into each of them is a wider change than this pass should make.

### Three races against the pool's own background work, none of which could be tested before

The drainer and the healer copy a whole file while the application is free to delete it, rename it or
overwrite it. Every one of those is a chance to resurrect something the user deleted, leave a file
under two names or none, or finish a copy of the OLD content over a newer write and call the pool
converged. The engine handles all three correctly — and that could not be shown until the throttle
fix above made the copies slow enough to interrupt.

Each scenario asserts its own premise before racing: the destination must NOT yet hold the complete
file, or the copy it means to interrupt has already finished and the test proves nothing. The first
version of the file had exactly that fault — a 12 MiB copy against a 6 MiB/s limit whose bucket
holds a second of credit was over in about a second, and all three scenarios passed while testing a
pool with nothing in flight.

- **Deleted mid-drain**: stays deleted, with no trace left on any member. A copy the pool still
  believes in is a file that comes back on the next mount.
- **Renamed mid-drain**: ends under exactly one name, with its content intact — not under both.
- **Overwritten mid-heal**: both copies end on the NEW content. This is the one that would actually
  lose data: a healer finishing after an overwrite leaves the older version on the member it was
  healing, the pool believes it is fully duplicated so nothing reconciles it, and which version a
  read gets is a coin toss.

### SMART said every disk was FAILING on any machine without root

`smartctl` cannot open a raw device unprivileged, which is the ordinary case for a daemon a user
starts. It still answers with well-formed JSON — an error in `smartctl.messages`, a non-zero
`exit_status`, and no `smart_status` object at all — and the parser read that missing field as
`passed: false`, i.e. as the drive having FAILED its own self-assessment. So every member of every
pool on such a machine was reported as a dying disk.

That is the worst direction for a health signal to be wrong in: indistinguishable from the real
thing, fired on hardware that is fine, and once it has cried wolf nobody believes the one that
matters. A drive that could not be ASKED is now `Unknown`, carrying smartctl's own words so "no
smartctl" can be told from "not allowed".

**And NVMe was invisible.** Health lived entirely in ATA attribute ids 5 and 197, and NVMe keeps
none of it there — it has `nvme_smart_health_information_log`. A drive at 96% of its rated endurance,
below its own spare threshold and logging media errors parsed as perfectly `Healthy`, which on
modern hardware is most drives. `percentage_used`, `available_spare` against the drive's own
`available_spare_threshold`, `media_errors` and `critical_warning` are all read now, and the drive's
stated thresholds are used where it states them.

`DiskHealth` gained an `Aging` step between `Healthy` and `Warning`, so the four-colour ramp the
dashboard needs is a real distinction rather than a UI invention: aging is wear that is normal and
worth watching, warning is degradation that has already happened, failing is get-the-data-off-it.

### Storage health is now visible where a user would look

The dashboard drew one dot per member, green for online and red for not — so a disk answering every
request while its spare blocks ran out looked exactly like a healthy one until it stopped answering
at all, which is far too late to be told.

Each member now carries ONE state, resolved in the daemon so the precedence is stated once rather
than reinvented in the browser: being evacuated or detached outranks anything SMART has to say about
a disk that is on its way out anyway. Green healthy, yellow aging, orange degraded, red failing (with
a slow pulse, and none for anyone who has asked for reduced motion), grey detached, violet being
replaced, and a hollow ring for unknown — deliberately an outline rather than a fill, so "we cannot
tell" never looks like "we checked and it is fine". A one-line reason from the drive's own counters
sits under any row that is not green, the tooltip carries the model and smartctl's verdict, and a
legend appears only when something is not healthy.

Three things make it trustworthy rather than decorative:

- **SMART is sampled off the hot path.** The metrics snapshot publishes every second and a smartctl
  query is a process launch allowed ten seconds; sampling on that path would put one fork per member
  per second in front of the timer the drain, the heal, the reload and the unmount request all share,
  so one unresponsive drive would stall pool maintenance. `MemberSmartCache` samples every five
  minutes on its own thread, never runs two sweeps at once, skips members that are offline, and turns
  any failure into `Unknown` rather than letting it reach the pool.
- **The state and the stylesheet cannot drift apart.** A state with no CSS rule would render as the
  default dot — a healthy-looking green for a drive that may be failing — and neither half's tests
  would notice. A scenario asserts every state the daemon can emit has a rule in the SERVED
  stylesheet.
- **The false alarm is pinned end to end.** A browser scenario asserts that on this machine, where
  SMART genuinely cannot be read, no member renders as failing.

**Verified against real drives, and the whole ramp end to end.** `smartctl -j -i -H -A` output
captured from this machine's NVMe is kept as a parser fixture, because it settled two things the
hand-written JSON could not: the drive has no `ata_smart_attributes` object AT ALL, so the old
parser had literally nothing to read on hardware of this kind, and `model_name` appears only when
`-i` is passed — which the query did not do, so every member was nameless in the report and the
tooltip. Replaying that captured output through the shipped daemon walks the whole ramp:

| what the drive reports | the dashboard shows |
| --- | --- |
| new, 0% used, 28 °C | healthy (green) |
| 80% of rated endurance, 52 °C | aging (yellow) — "80% of rated endurance used; running at 52 °C" |
| 93% used, 4 media errors | warning (orange) — "4 media error(s); 93% of rated endurance used" |
| 4% spare against a 10% threshold | failing (red) — "…; 4% spare blocks left" |
| member unplugged | detached (grey), which correctly outranks the failing reading underneath it |

**A deployment limit worth stating plainly: SMART needs privileges.** `smartctl` cannot open a raw
device unprivileged, so a daemon a user starts reports `Unknown` for every member — honestly, and
with the reason in the tooltip, but it is still no health data. Getting real readings needs the
service run with privilege, a capability granted to smartctl, or a udev rule; none of that is the
pool's to arrange, and the honest Unknown state is what makes the limit visible instead of silently
green. The three "could not read" cases now say WHICH they are — smartctl missing, not permitted, or
timed out — because they call for three different actions and all three used to arrive as the same
bare "SMART not available".

**Not built, deliberately: the blue "online spare".** There is no spare concept in the product — no
role, no manifest field, nothing that holds a disk in reserve and no rebuild that would claim one —
so a blue dot would have been a colour with nothing behind it. It is a real feature (a role that
placement must never target, and a trigger that promotes it when a member fails) and belongs in its
own change.

### A file could not be created when the chosen disk had gone read-only

The failure real filesystems produce: ext4 hits a write error and remounts itself read-only. Such a
member is not gone, so the online probe keeps it; it does not fail everything, so the fault cooldown
never parks it; it reads perfectly and refuses every write.

`Create` chose ONE placement target and failed outright if that member would not take the file, so
roughly half of all new files died with a bare "access denied" while a perfectly writable member sat
beside them. The write path has redirected around a storage that fails mid-write for some time
(SAFE-DEGRADE); creation had no such path. It now retries, noting the fault — which sinks that member
in the readiness order placement itself chooses by, so the retry lands elsewhere and so does every
create after it until the member recovers. Mutation-checked: pinning the retry back to one attempt
fails the scenario.

**Still open, and it is a durability decision rather than a bug.** A DUPLICATED pool still refuses
new files while a member is read-only, because it cannot make the second copy. The degraded-write
path that exists for exactly this — "one lost drive degrades redundancy, not availability" — keys on
a member being UNREACHABLE, and a read-only member is reachable. Extending it would mean accepting
writes at a lower duplication than configured whenever a member merely refuses them, which weakens a
guarantee under a condition the product currently treats as fatal. That is the owner's call, not a
passing fix; the current behaviour is pinned so the decision is made deliberately.

### A member that goes SLOW was almost invisible to the engine that was slowing it

The resilience work so far breaks members cleanly: the disk vanishes, or it errors on every request.
Both are easy to handle well because both are unambiguous. A drive that is merely DYING does neither
— a controller retrying internally, a link renegotiated down, a NAS behind a saturated uplink. It
answers every request correctly, eventually, so it never errors, never cools down, and nothing about
it looks wrong except that it takes a hundred times longer. That case was not covered at all, and
three separate things were wrong with it.

**1. A live reload ignored the rate limits.** `maxIops`/`maxThroughput` were read once, when the
engine built its members. A live reload re-read the config, the member roles and the duplication
level, so everything else in the manifest could be changed under a mounted pool and this one thing
silently could not — the setting whose entire purpose is the situation you cannot unmount for ("the
pool is taking too much of that disk, ease off"). `UpdateMemberLimits` now applies them, beside the
existing `UpdateMemberRoles`.

**2. The pool could not see a member it was itself holding back.** The throttle waits in
`VolumeQueues.Enter`, BEFORE the volume is touched, so a limited member performs each operation as
briskly as ever and only the queue knows better. `MeasuredVolumeIO`'s latency EWMA therefore saw a
perfectly healthy member. A genuinely slow DEVICE was always visible, because its latency is real and
lands inside the measurement — but a member the pool was deliberately limiting was not, which made
the documented promise beside that code ("a throttled member simply receives less work, and new files
go elsewhere") false. `VolumeQueues` now keeps a decaying per-member average of what the limit is
costing, and `_LoadScore` counts it.

**3. The readiness score treated one queued operation as one second.** `inflight * 1000 + latency`
made "one request outstanding on a healthy member" score exactly the same as "every operation on this
member takes a full second". On a healthy pool that is harmless and it is what spreads load. With one
member sick it is the whole problem: the fast member scored 1000 whenever it was momentarily busy,
the permanently-slow one scored about the same, and a duplicated read kept being handed back to the
slow copy. The score is now `(inflight + 1) x per-operation cost`, which keeps the load-spreading
(idle members still separate by queue depth) and makes a slow member lose to a busy fast one.

Measured through a real mount, 48 MiB read of a duplicated file with the member holding the PRIMARY
copy collapsed to 1 MiB/s — serving it entirely from the sick member would take about 48s:

| | 48 MiB duplicated read |
| --- | ---: |
| both members healthy | 0.04 s |
| one member collapsed, as found | 8.02 s |
| after ordering non-split reads by readiness | 7.00 s |
| after the throttle became visible, and the score multiplied | **1.03 s** |

So the answer to "do we recover using the other storage, or drop hard?" is: we now recover, and we
did not before. Placement moved the same way and more sharply — a burst of 12 files split 6/6 across
a healthy and a collapsed member before the fix and **11/1** after, the burst itself dropping from
11.0 s to 1.1 s.

**Writes are a different answer, and it is a deliberate one.** A duplicated write IS paced by its
slowest copy: 8 MiB took 7.01 s with one member at 1 MiB/s, which is exactly that member's rate.
That is `write.minCopiesBeforeAck` defaulting to 2 — an acknowledgement means the bytes are on both
disks (SAFE-NOLOSS) — and it cannot be configured away, because the pool refuses to mount with an ack
floor below the duplication level. The sanctioned escape is the RAM-ack opt-in
(`write.policy: performance` + `acceptVolatileAck`), which took the same write in **0.01 s**. All
three are pinned: the pacing, the refusal, and the opt-in.

### What a failing member actually costs, measured across the whole matrix

Six failure modes against seven kinds of storage — RAM, a simulated SSD, HDD, SD card and cloud
endpoint, and the host's real devices — 42 cells, all green. The invariant in every cell is the same
and holds: nothing acknowledged is lost, the mount survives, the cost is bounded. The numbers are the
point, though, because "the pool survives a disk going" is a claim and "the worst operation cost
72 ms" is an answer:

| Failure | Worst single operation, across every storage kind |
| --- | ---: |
| member pulled, reads served from the survivor | 11–14 ms |
| member pulled mid-write | 2 ms simulated, **72 ms** on the real SD card |
| member present but erroring on every request | 18–21 ms |
| power cut, pool restarted | 18–20 ms |
| power cut with a member also missing | 18–22 ms |

Two things are worth drawing out. **Failover costs milliseconds, not seconds** — well under the
five-second fault cooldown, because a member that has genuinely gone is filtered out by the online
probe and never tried at all; the cooldown is for the harder case where the disk is still THERE and
failing, and even that costs about 20 ms per read. And **the storage underneath barely moves the
number**: the only cell above 22 ms is the real SD card taking a write when it was pulled, which is
the one case where slow hardware genuinely had more work in flight when it went.

### A member reached through a symlink reported the WRONG disk as its failure domain

Found while trying to measure two devices, and it is the reason the first attempt measured one.

`HostEnvironment._GetPhysicalVolumeId` started from `Path.GetFullPath`, which does not follow
reparse points, and then matched that path against `/proc/mounts`. A member reached through a
symlink or a junction therefore reports the disk the LINK sits on rather than the storage it names.
Two members linked from one filesystem onto two genuinely different disks collapse into a single
failure domain, and everything keyed on that identity follows: placement declines to spread
redundant copies across them (over-conservative, so safe but wasteful), the health report counts one
independent domain where there are two, and — the part that costs throughput — `VolumeQueues` hands
the pair the queue depth of ONE device, so the two disks take turns instead of working at once
(§6.4, FR-PAR). Observed directly as `! Members '…/m0', '…/m1' share one physical volume` for a pool
with one member on each NVMe drive.

Fixed by resolving the reparse point before deriving the identity, on both platforms. The engine
suite is unmoved by it (641 pass either way) because its members are ordinary directories; what it
changes is any pool whose members are linked, which is exactly how removable and multi-disk layouts
tend to be wired.

### Measured on two real devices, which is what the scatter work could not do

`docs/Performance.md` prices overlapped I/O honestly and says where it stops: its "2 copies" row
shares one device, so it "prices the split's overhead rather than the gain", and the split "can only
pay for itself on hardware that actually has two". Also worth stating plainly, because it changes how
every earlier number reads: on this host **`/tmp` is tmpfs**, so a pool with both members in the temp
directory has both of them in RAM. Those runs price the code path, not a disk.

With members on two independent NVMe devices, 384 MiB read cold through a real mount, host page cache
evicted per file with `posix_fadvise(DONTNEED)`, and the failure-domain fix above in place so the
engine can actually see two devices — the same comparison, three times:

| 384 MiB cold sequential read | spread over two devices | both copies on one |
| --- | ---: | ---: |
| quiet machine | 2,428 MiB/s | 2,260 MiB/s |
| quiet machine, after the failure-domain fix | 2,714 MiB/s | 2,481 MiB/s |
| inside the full battery, machine loaded | 1,508 MiB/s | 1,972 MiB/s |

**So the honest answer is that this host cannot separate the two arrangements.** +7%, +9%, −24%: the
sign tracks what else the machine was doing rather than which arrangement was used. Both figures sit
at or below the engine's own cached-read ceiling (~2,470 MiB/s in the matrix), so neither is
device-bound and the split is not what limits either of them. The first two runs, taken alone, would
have gone into this file as "the split pays for itself by 1.09x" — which the third refutes.

The scenario therefore asserts no ratio at all and is `[Explicit]`, like the performance matrix: it
prints the numbers for someone running it deliberately on a quiet machine. An assertion tuned until
all three of those runs passed would have been measuring nothing and reporting it in green.

A write burst across a two-device tier DOES reach both disks, and that one IS asserted, because it
does not depend on a clock: 8 files of 48 MiB landed 5 on one device and 3 on the other. Where the
files land is the claim underneath the throughput and is the half a loaded machine cannot distort.
An earlier version asserted the rate instead, read 156 MiB/s against a device probe that fluctuated
between 125 and 171 MiB/s on the same hardware within the hour, and would have recorded "the tier
does not combine" from what was really a noisy probe and an unlucky split.

**The harness was leaving the machine littered, which is worth recording because it hid nothing and
cost real disk anyway.** Killing a mount process does not remove the kernel's FUSE entry: the
mountpoint stays in `/proc/mounts` answering "transport endpoint is not connected" until something
detaches it, and only the deliberate-crash path ever did. A day of runs left **94** dead mounts and
94 undeletable temp roots behind. `Dispose` now runs the same lazy detach, and a full battery
finishes with zero of either.

The eviction is not taken on trust: the same machinery measures the SD card at **14.4 MiB/s read**
where an unevicted read of the same file reports 5.6 GB/s. A 400x gap is what a working eviction
looks like, and without it every number above would be memory bandwidth.

Genuinely different devices also make two things testable that a one-disk host cannot reach, and both
behave correctly: a 119 MiB member filled right up refuses further writes cleanly, keeps the mount
alive and leaves everything already stored byte-identical; and a member that is present but FAILS
every operation (permissions revoked on its storage root, so the online probe still sees it) is routed
around rather than waited on, which is the case the five-second fault cooldown exists for and which
nothing exercised end to end before.

## Still open

Two items that stood here are closed; they moved to "Closed, kept as a record" below rather than
being deleted, because what they cost is the point.

1. **A file replaced by rename keeps serving its OLD content to new readers — Windows.** After
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
   reads is pinned by an open handle and outlives the rename underneath it.

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
2. **`_RenameFolder` holds leases on the two folder paths but on NO CHILD FILE while moving them.**
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

## Closed, kept as a record

An earlier revision of this file claimed "nothing known" under "Still open". That was wrong — it
dropped the two UI items below. A capability gap that is merely WRITTEN DOWN reads as a decision,
and these two sat there through several passes looking like ones, so they are kept here rather
than deleted.

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
- `MemberFailureLatencyEndToEndTests` — what losing a disk COSTS, as opposed to whether it is
  survived. A pool that answers correctly after a thirty-second pause has lost no data and is still
  unusable. A member pulled mid-stream must leave every remaining chunk bounded; a member that is
  still PRESENT and fails every operation (its storage root's permissions revoked, so the online
  probe cannot simply drop it) must be routed around rather than waited on, which is the case the
  five-second fault cooldown exists for and which nothing exercised end to end; a drain interrupted
  by losing its target must leave the file on one tier or the other and never on neither, and no
  read may SUCCEED with fewer bytes than the file holds while the disk comes back.
- `BrownoutEndToEndTests` — the member that goes SLOW rather than away, applied LIVE without a
  remount because that is how it happens. Reads must be served from the healthy copy instead of
  crawling with the sick one; new files must stop being placed on it; throughput must come BACK when
  it recovers, rather than the degradation latching. Its first scenario is the premise — a limit
  lowered under a mounted pool has to take effect at all — and the write scenarios pin the durability
  trade from all three sides: the pacing, the refusal to weaken the ack floor, and the RAM-ack opt-in
  that is the sanctioned way out.
- `StorageFailureMatrixEndToEndTests` — the cross product of every way a member can fail against
  every kind of storage it can fail on: pulled and gone, pulled mid-write, pulled and returned,
  present but erroring on every request, power cut, and power cut WITH a member also missing —
  each against RAM, a simulated SSD/HDD/SD-card/cloud, and whatever real devices the host has. The
  failures a product has to survive are not independent of the storage under them: a disk pulled
  from a rate-limited tier has far more work still in flight when it goes. One invariant in every
  cell — nothing acknowledged is lost, the mount survives, the cost is bounded — and the timings are
  PRINTED, because "what happens when a disk goes" is a question with a number for an answer.
- `SimulatedDeviceEndToEndTests` — the same tiering questions on storage of KNOWN speed, using the
  product's own `maxIops`/`maxThroughput` member limits. Real devices are the honest evidence and a
  poor experiment (a host has whatever disks it has, and CI has one); a limit is identical on every
  machine and models hardware nobody has to hand. Its first two scenarios are the PREMISE, pinned
  from both sides: a limited member really is held to its rate, and the same pool unlimited really is
  far faster. Without both, every scenario built on a limit would pass while measuring an unlimited
  disk.
- `HeterogeneousDeviceEndToEndTests`, `MultiDeviceThroughputEndToEndTests` — pools spread over
  storage of genuinely different speed, which is the only way the tiering claims can be falsified at
  all. They self-ignore on a machine with one disk, and the slow device is found by MEASURING
  candidates rather than by guessing from paths, because a second internal NVMe and a card reader
  are both mounted under `/run/media` and are indistinguishable by name. Two things only real
  hardware reaches: a member filled right up refuses further writes cleanly and keeps everything
  already stored byte-identical, and a removable disk that comes and goes repeatedly leaves every
  file whole with reads still bounded.
- **`DriveBender.EndToEnd.Tests`** — the SHIPPED `dbmount` binary, with no project reference to the
  engine, on both targets in CI: a real pool mounted through WinFsp/Dokan or FUSE and driven
  through `System.IO`; the management API over HTTP; and the page itself in Chromium. This tier
  exists because the engine suite drives an in-memory fake and therefore stayed green through
  `5b67a05`, in which mounting any local pool was impossible. `DBE2E_REQUIRE_DRIVER=1` makes a
  missing driver a failure, so the suite cannot report green by skipping everything.
