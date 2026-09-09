# When somebody messes with the storage

Every other part of this design assumes the members hold what the pool put there. This one assumes
the opposite, because that is what happens on real machines.

The member disks are ordinary folders on ordinary filesystems. Anyone with access to them can browse
in and "clean up" a hidden folder, point a sync tool at a disk, restore a member from a backup, open
a sidecar in a text editor and save it, or pull a drive. None of these are crashes and none are
bit-rot — they are edits, made by something with every right to write to that disk and no idea what
the files meant.

## The bar

Three rules, in order. They are deliberately not "the feature keeps working".

1. **Never lose acknowledged data.** A file the pool accepted is readable afterwards. Absolute;
   everything else may degrade.
2. **Never crash the mount.** A damaged sidecar takes out that sidecar, not the filesystem.
3. **Degrade legibly.** Where a promise cannot be kept, say so, with an error the caller can see.

Rule 3 is the one that is easy to get wrong in the *comfortable* direction. Substituting different
content for what was asked for is worse than failing, because nothing downstream can detect it. A
snapshot that has lost its stored version and quietly answers with the live file has not degraded —
it has lied, and a restore driven off that answer overwrites the good copy with the newer one.

## What the pool holds, and what damaging it costs

| Its own file | If it is damaged, deleted or stale |
| --- | --- |
| `journal.jsonl` | Mirrored on every member. Unparseable lines are skipped, a missing file means "nothing was interrupted", and no journal ever costs a file — it records what the pool was *doing*, never what it holds. |
| `tombstones.jsonl` | Mirrored. It records what an ABSENT member owes; losing it can cost catch-up work, never a file. |
| `checksums.json` | Loses the ability to *detect* bit-rot on those paths until the next scrub rebuilds it. Never a file. |
| `trash/*.trashinfo` | That entry loses its origin. The bin still lists, and every other entry still restores. |
| `snapshots/index/*.json` | That snapshot is gone — not half there. Live files are untouched, and the store keeps working. |
| `snapshots/versions/*.snapver`, `*.snapinfo` | That version is unavailable. Reading it through the view **fails**; see below. |
| The whole `.drivebenderutility` folder on one member | Nothing, on a duplicated pool. The journal and the tombstone log are mirrored precisely so this is survivable. |

## Two rules the tamper suite had to introduce

Both existed because a test found the pool doing the opposite. Both are narrow, and both are about
rule 3.

### A snapshot will not serve the live file in place of a lost version

A snapshot copies nothing when taken, so most of what it holds *is* the live file, and reading
through to it is what makes a snapshot of an idle pool free. The danger is the same fallback reached
for the wrong reason — a disk pulled, a sidecar deleted, somebody tidying a hidden folder.

The proof that the fallback is honest is the file's own modification time: **a file not written since
the snapshot was taken cannot have changed since the snapshot was taken.** Anything newer has
changed, so the live file is definitively not the snapshot's content, and the missing version is
reported instead of papered over.

It is durable, needs no extra bookkeeping, and cannot itself be invalidated by damage to the snapshot
store — which is exactly the property required, since the store is what is under suspicion.

### Recovery will not roll an intent forward over newer content

An intent is written before an operation and completed after it, so a crash can leave one behind that
genuinely needs finishing. A journal can also arrive from somewhere else entirely: a member restored
from a backup, a hidden folder copied across, an old `.drivebenderutility` put back by hand. Then it
describes work that finished long ago, against a path that has since been recreated — and finishing
it deletes a file nobody asked to delete, with the recovery machinery itself as the cause.

Time tells them apart. In a real crash the file was written *before* the delete was logged, so its
mtime is older than the intent; a file whose mtime is *newer* than the intent cannot be the content
the intent was about. Intents therefore carry the time they were logged, and a destructive
roll-forward (delete, rename, trash-move) is declined when the content it would destroy is newer.

An intent with **no** timestamp — written by an older version, or forged — proves nothing about when
it was made and never gets to destroy anything. Declining costs an operation the caller was never
told had succeeded. Proceeding costs the file.

## A dying disk is not a missing one

Three failure shapes, treated differently, and the tamper suite covers all three:

- **Pulled.** The member vanishes; the online probe filters it out and it costs nothing afterwards.
- **Dead controller.** Every operation fails while the member is still "there". A fault cooldown
  parks it at the back of the readiness order so one bad member cannot stall the pool.
- **Remounted read-only.** Reads are perfect, writes all fail. This is the commonest of the three in
  the field — ext4 does it on the first I/O error — and the hardest, because the member looks healthy
  to anything that only reads and is still holding the pool's data, so it must keep being read from
  while nothing new is placed on it.

The third one produced two fixes. Placement now consults the same fault record the engine keeps, so a
member that just refused a write is not chosen again for the next one — the create path always
retried on the stated understanding that it would land elsewhere, but the record lived in the engine
and placement could not see it, so every retry picked the same member. And a member that refuses the
*duplicate* copy now defers it, exactly as a member with no room already did: failing the whole write
there loses a file the pool could perfectly well have stored on a healthy member, for the sake of a
redundancy the healer exists to restore.

What is *not* routed around is the acknowledgement policy. A pool configured to acknowledge nothing
until two copies are down cannot honour that with one disk frozen, and the honest answer is to refuse
the write. Acking anyway would tell the application its data is redundant when it is not — which on a
backup target is the whole proposition quietly evaporating.

## Where the tests are

- `DriveBender.EndToEnd.Tests/TamperEndToEndTests.cs` — the real thing: a mounted pool, the members
  edited behind its back, driven through the shipped binary.
- `DriveBender.Vfs.Tests/Unit/TamperResilienceTests.cs` — the same rules where they are cheap to check
  and impossible to misattribute.
