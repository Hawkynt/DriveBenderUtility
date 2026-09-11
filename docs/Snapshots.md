# Snapshots — a design, before any code

Written before any code, because a screen over a feature that does not exist is worse than no
feature. The most important section here is the one about what this approach cannot do.

Nothing here is implemented. Every occurrence of "snapshot" in the codebase today is the unrelated
metrics snapshot.

The mechanism below is not the one this document first proposed. The first draft copied a file's old
content aside before the first write to it, which is both expensive and unnecessary: the engine
already publishes a new file by renaming a staged temp into place, and hanging the design on that
point turns the common case from a full-file copy into two renames. The cost section is rewritten
around that — the first draft stated the expensive case as if it were universal, which would have
argued the feature out of existence for workloads it suits perfectly well.

## What the storage model allows

The decision is made by a property this pool already has, and should keep: **a member holds real
files, in real directories, on a real filesystem.** That is what makes a pool recoverable with a file
manager and nothing else, and it is the compatibility contract with the Drive Bender on-disk layout
(SAFE-COMPAT). Every engine path is whole-file — `WholeFilePublisher`, `PhysicalCopy`,
`ResolveCopies`, the drainer, the healer, the media operations. Blocks exist only in the read cache.

So there are two candidate designs, and only one of them is compatible with that.

**Block-level copy-on-write** — the thing "CoW" usually means — requires the pool to own an extent
map: file content addressed by block, blocks shared between versions, reference counts per block. A
member would stop holding recognisable files. That is a different product: it would break SAFE-COMPAT,
make a member unreadable without the engine, and rewrite placement, duplication, healing, draining,
scrubbing and recovery, all of which are written in terms of whole files. It is not an increment.

**File-level copy-on-write** is the one that fits, and the machinery is already here. `PoolTrash`
moves a version of a file aside into `.drivebenderutility/trash` on its own member, names it with a
uniquifying token so successive versions coexist, writes a sidecar recording where it came from, and
restores it on demand under a journalled intent. That is the storage half of a snapshot store,
working and tested. What is missing is the pinning, the refcounts, and the naming.

The rest of this document is file-level, and its costs are stated plainly rather than buried.

## The shape

A **snapshot** is a name, a timestamp, and the set of file versions live at the instant it was taken.
Taking one copies no data: it records the namespace, so its cost is proportional to the number of
files, not their size. On a pool of a million files this is a metadata write, not an hour of I/O.

A **version** is a file's content as it was. The live file is the current version. Older versions
live in a per-member snapshot store, alongside the trash and using the same mechanics.

The rule that makes it work: **a file that at least one snapshot pins is written through the staging
lifecycle, and the old version is moved aside when the staged file is published.** Not on the first
write — at the publish, which is a point the engine already has.

This is worth being precise about, because the obvious design is worse. "Copy the old content aside
before the first modification" costs a full-file copy and puts a check on the write path. The engine
already has the better mechanism and uses it for every newly created file: `_staging` holds the paths
being written through a temp name, `_DataName` transparently routes every read and write of such a
path to that temp, and `Close` renames the temp into place once the last handle goes. Staging is
keyed on the path being in that set — not on the file being new — so an existing file can be routed
the same way by putting it there.

Publishing a pinned file then becomes two renames on one filesystem: the live file into the snapshot
store, the temp into the live name. **No bytes are copied at all.** The store is
`.drivebenderutility/snapshots/…` on the same member as the file, so both renames are metadata
operations, and the version that lands in the store is the original file itself rather than a
duplicate of it.

"Modification" is also delete and rename. Delete already moves the file aside instead of unlinking it
when the trash is on, which is the same operation against a different destination. Rename is the
awkward one and is discussed below.

Reading a snapshot resolves a path to a version token: either a version in the store, or the live
file when nothing has touched it since. Most files in a backup pool are never rewritten, so most of a
snapshot is the live file and costs nothing at all.

## What this cannot do, and who it hurts

The cost divides into two cases, and only one of them is expensive. An earlier draft of this document
stated the expensive one as if it were universal, which was wrong and would have argued the feature
out of existence for workloads it suits perfectly well.

**A whole-file rewrite costs nothing.** Truncate-then-write is what `File.WriteAllBytes` does, what
every save-to-a-temp-and-rename editor does, and what backup tools do. The writer supplies all the
bytes, so the staged temp is complete on its own and publishing is the two renames above. The old
version reaching the store is free, and the snapshot store grows by the size of the old file without
that file ever being read.

**A partial in-place modification costs the bytes the writer did NOT touch.** Writing one byte at
offset 0 of a 40 GB file leaves the staged temp holding one byte and 40 GB of nothing, so the
remainder has to come from the old version before the temp can be published. That is a real copy and
file granularity offers no way around it. The write buffer already records which ranges were written
(`PendingOp` carries offset and length), so it is the untouched remainder rather than the whole file
— which for a small edit to a large file is very nearly the whole file, and the distinction is
honest rather than comforting.

So: a pool holding documents, photos, archives and backup sets pays almost nothing. A pool holding
virtual machine disks or database files, which are large and rewritten in place, pays close to full
size per snapshot per file. That must be said in the UI, not just here, and it is an argument for the
size ceiling and path exclusions below rather than against the feature.

Two mitigations are worth having and neither is a fix:

- A size ceiling and a path-pattern exclusion in the snapshot policy, so the pathological files can
  be left out deliberately rather than discovered in a full-disk incident.
- Reporting, per snapshot, what it is actually costing, so the answer to "why is the pool full" is on
  the screen rather than in a support conversation.

**A snapshot is not a backup.** It shares the pool's failure domains: it protects against deletion
and overwriting, not against losing the disks. The UI must say so in those words. A product that
lets an operator believe otherwise has done them more harm than having no snapshots.

## The pieces

**Store.** `.drivebenderutility/snapshots/<snapshot-id>/` per member, sibling to the trash, with the
same uniquifier discipline. Versions live under it exactly as trashed files do today.

**Index.** Per snapshot: id, name, created-at, and the path → version-token map. It is the one piece
with no existing analogue. It has to be journalled, mirrored across members like the manifest is, and
survive a member being absent — the same problem the tombstone log solves, and its answer ("apply
what you can, keep what you could not, retry later") is the model.

**Refcounts.** A version is pinned by every snapshot that names it. Deleting a snapshot decrements;
at zero the version is deleted. The refcount lives with the version, not in a central table, so a
member that was offline during a snapshot deletion converges when it returns rather than leaking
forever.

**Reserve.** A configurable ceiling on the store, per pool, with the same shape as the trash's
`retention`/`maxSize` and the existing per-member `ReserveBytes`. Placement and `StatFs` already
subtract a reserve; the snapshot store must be counted the same way, or a pool reports free space it
has already promised. When the ceiling is reached the policy is the operator's: refuse new snapshots,
or drop the oldest. Both are defensible; silently doing either is not.

## Where it touches the engine

Honestly enumerated, because this is the part that decides the schedule:

- **The write path.** Nothing on the hot path. The question "is this pinned" is asked once, when a
  file is opened for writing, and its answer decides whether the path joins `_staging`. Every write
  after that is the staged path the engine already runs for new files. This is the whole reason to
  hang the design on staging rather than on a copy-before-write hook: a pool with no snapshots pays
  one set lookup per open and nothing else.
- **Delete and rename.** Both destroy a version. Delete already has the trash to reuse — the same
  aside-rename against a different destination. Rename is the awkward one, because a snapshot names a
  path and the live file no longer has it: either the version is moved into the store at rename time,
  or the index has to follow the file, and the first is simpler and matches what delete does.
- **The drainer, the healer, the media operations.** All relocate whole files. A pinned version in a
  member's store must relocate with it, or be pinned in place. This is the most dangerous interaction
  in the list and it is not hypothetical: writing this document turned up the same bug in the trash,
  which is the store's existing analogue. `ScatterAndRemove` walks the visible namespace, that walk
  skips the pool's hidden tree, and so removing a member silently destroyed every recoverable file in
  its recycle bin — a verb whose whole promise is that a member's data is scattered before it leaves.
  Found, reproduced and fixed while writing this section. A snapshot store put in the same place will
  meet the same walk, and the test for it should exist before the store does.
- **Recovery.** Taking a snapshot, deleting one, and every aside-move need journal intents. The
  half-finished states are: index written but versions not pinned, versions pinned but index not
  written, aside-move done but the write that triggered it not.
- **Reads.** A snapshot view is a second resolution path beside `ResolveCopies`. It should be
  read-only and share the failover and short-read guards rather than growing its own.

## Delivery, in slices that are each worth having

1. **Store and index, no UI.** ✅ **Done.** Take, list and delete a snapshot; preservation on
   overwrite, delete and rename; refcounts; journalled throughout; nine engine tests. The feature is
   real and reachable only from the engine — there is no verb and no screen, on purpose.

   What the implementation confirmed, and what it changed:

   - The cheap path works as designed. Creating over an existing file WITH truncate preserves by
     renaming the old file into the store and then takes the ordinary new-file branch, so the
     replacement is staged and published by a rename exactly as any new file is. Two renames, no
     bytes.
   - Opening for writing WITHOUT that promise preserves by copy. It has to: the caller may modify in
     place, and the untouched bytes must survive in the live file as well as in the version.
   - Rename was the one this design nearly missed. It destroys a snapshot's view of BOTH endpoints
     and neither is obvious — the source path stops naming the content, so a snapshot that recorded
     it would resolve to a live file that is no longer there, and a rename ONTO a file is a delete
     wearing a different verb. Both are preserved now, by copy, because the live file has to stay
     put for the rename itself to have something to move.
2. **Read a snapshot.** ✅ **Done.** Browse what a snapshot recorded, open a file as it was, and
   restore one over the live file. Browsing distinguishes a path whose content had to be set aside
   from one nothing has touched since — for the second the live file IS the snapshot's content, and
   showing them identically would make a snapshot of an idle pool look empty. Restoring is a write
   like any other, so the version it overwrites is itself preserved for any newer snapshot that
   still needs it.
3. **Reserve and policy.** ✅ **Done.** `snapshots.reserve` (default 10%) with
   `onReserveFull: drop-oldest | refuse`. The ceiling is enforced BEFORE the aside that would breach
   it, not after — "after" means the pool can always be pushed one arbitrarily large file past its
   limit. Dropping is oldest-first, because the promise worth keeping is that RECENT history is
   available; it never drops the only snapshot, since that is not a policy but a feature switching
   itself off. Per-snapshot cost is reported by the listing, which is the number an operator needs
   before deciding what to drop.
4. **The interactions.** ⚠️ **Partly done.** `ScatterAndRemove` now moves the snapshot store off a
   member that is leaving, which was the risk this slice was named for and is the same bug the
   recycle bin had — covered end to end. Still outstanding: what a *replace* does with the store,
   and a crash-mid-aside scenario. The drainer and the healer relocate live files rather than
   versions, and a version is only ever created from a file that is settled, so those two are
   believed safe rather than demonstrated — which is a distinction worth keeping in writing.
5. **The UI.** ✅ **Done.** A Snapshots panel on the pool card — only when the pool is mounted,
   because the engine is what keeps a snapshot honest. It lists each snapshot with what it is
   holding, takes and deletes, and browses one to restore a single file. It opens with the sentence
   that matters most and that nowhere else will say: a snapshot is not a backup, because it shares
   these disks.

6. **A place inside the pool.** ✅ **Done.** Every snapshot is a folder in the mounted namespace:
   `.snapshots/<name>/<path>`. Recovering a file is a copy in a file manager — no verb, no screen,
   no administrator — which is the difference between a feature an operator uses and one a user
   does.

   Three properties make it safe, and each of them is load-bearing:

   - **Not listed.** The tree resolves when asked for by name and does not appear in a directory
     listing. That is what ZFS does with `.zfs` and for the same reason: a backup or sync tool walks
     the pool, and a visible view would have it copy every version of every file the pool has ever
     held, on every run. A hidden-but-navigable tree is the only shape that can be left switched on.
   - **Read-only, loudly.** Every mutation door — create, open-for-write, unlink, rename either
     way, mkdir, rmdir, setattr, and write or truncate through a handle already open — refuses with
     `AccessDenied`, which reaches the application as `EACCES`. Silently accepting a write into the
     past would be the worst available answer: the caller believes it edited history and nothing did.
   - **Its own handle space.** Snapshot handles are negative and live outside the ordinary handle
     table. That table is keyed on live pool paths and carries leases, write buffers, read-ahead and
     staging — every one of them about a file the pool can still change. Letting a path that does
     not really exist into it would mean each of those mechanisms has to learn about the view.

   Two things the implementation had to get right that the design did not say. A file *nothing has
   touched since the snapshot* has no preserved version, so the view falls through to the live file
   — without that, a snapshot of an idle pool would appear to contain only the files that happened
   to change afterwards. And a *folder* in the view resolves to no version and no live file for
   exactly the same reason a lost file does; answering `NotFound` with "neither a version nor the
   live file remains" would report data loss where there is none, so the view asks whether anything
   lives beneath the name before reaching for that message.

   A pool that already contains a real folder named `.snapshots` finds it shadowed by the view. That
   is a known and accepted collision, in exchange for a path users can guess.

The order is deliberate: every earlier slice is useful without the later ones, and the screen comes
last because a screen over a half-built engine is the mistake this project has already made twice —
`ChMod` answering success and storing nothing, `ChOwn` doing the same. Both were dangerous precisely
because the surface said one thing and the storage did another.

## What I would want decided before slice 1

- **Do the intended pools hold anything large that is rewritten IN PLACE?** Not "large files" —
  large files that are modified rather than replaced. Documents, photos, archives and backup sets
  are replaced wholesale and cost nothing here. Virtual machine disks and database files are
  modified in place and cost close to their full size per snapshot. If the pools hold the second
  kind, the answer is to not build this rather than to build it and document a sharp edge.
- **Reserve exhausted: refuse, or drop the oldest?** It decides whether a snapshot is a promise or a
  convenience, and every later slice depends on which. **Answered by default rather than by
  decision:** the ceiling is 10% and the policy is drop-oldest, both configurable. If snapshots are
  meant to be a promise rather than a convenience on these pools, `onReserveFull: refuse` is the
  other half of that question and it is one setting away.
- **Scheduled snapshots, or manual only?** Scheduling is small once the engine exists, and it changes
  the reserve maths from "an operator's choice" to "a rate".
