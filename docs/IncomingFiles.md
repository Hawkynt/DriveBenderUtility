# How an incoming file is written

This is the path every new file takes, from the moment an application creates it until it sits
whole, under its real name, on as many disks as its folder's duplication level asks for. It
applies to **new files and whole rewrites** (a save that replaces a file, a copy into the pool).
Editing an existing file in place (a database, a VM image) writes into the file where it lies and
is not covered here.

Two decisions are made, at two different times:

1. **Where the file goes** (group and placement strategy) is decided **once, when the file
   arrives**, before its first block is written. It changes only if a disk fails during the
   write, or the file outgrows the landing zone.
2. **Which disk takes the next block** is decided **per block, while the file is written**, by
   the file's *stripe session*.

## The picture

```
                        application creates "photos/img.raw"
                                        |
                                        v
  +------------------------------------------------------------------------+
  |  1. ARRIVAL: decided once, before the first block                      |
  |                                                                        |
  |   landing zone configured?  --no-->  storage group                     |
  |          | yes                                                         |
  |          v                                                             |
  |   file fits the landing zone? --no-->  storage group                   |
  |   (size as far as it is known:                                         |
  |    copy tools announce it up front)                                    |
  |          | yes                                                         |
  |          v                                                             |
  |   landing-zone group                                                   |
  |                                                                        |
  |   Inside the chosen group the placement strategy picks the FINAL       |
  |   disks: the primary, plus one disk per extra copy the folder's        |
  |   duplication asks for, never two on the same physical disk.           |
  |   Every other disk of the group becomes a HELPER.                      |
  +------------------------------------------------------------------------+
                                        |
                                        v
  +------------------------------------------------------------------------+
  |  2. WRITING: the stripe session, block by block                        |
  |                                                                        |
  |   client blocks:  [0][1][2][3][4][5][6][7] ...                         |
  |                     \  |  /    \  |  /                                  |
  |                      first disk of the group that can take it          |
  |                      (least queued, fastest; skips blocks already      |
  |                       in flight or written elsewhere)                  |
  |                     /        |          \                              |
  |          +-----------+ +-----------+ +-----------+                     |
  |          | disk A    | | disk B    | | disk C    |   <- the group      |
  |          | (final)   | | (final)   | | (helper)  |                     |
  |          | img.TEMP  | | img.TEMP  | | img.stripe|   temp names only:  |
  |          | 0 . 2 . 4 | | . 1 . 3 . | | . . . . 5 |   nothing carries   |
  |          +-----------+ +-----------+ +-----------+   the real name yet |
  |                                                                        |
  |   A block map records which disk holds the current bytes of each       |
  |   block. The client's write returns once each of its blocks is on as   |
  |   many disks as the folder's ack count (2 for duplicated folders).     |
  +------------------------------------------------------------------------+
                                        |
                                        v
  +------------------------------------------------------------------------+
  |  3. FILLING: every final disk collects the blocks it is missing        |
  |                                                                        |
  |          disk A  <-- 1, 3 from B,  5 from C   (or from RAM, if held)   |
  |          disk B  <-- 0, 2, 4 from A,  5 from C                         |
  |                                                                        |
  |   A block rewritten meanwhile is simply owed again.                    |
  +------------------------------------------------------------------------+
                                        |
                                        v
  +------------------------------------------------------------------------+
  |  4. CLOSE                                                              |
  |                                                                        |
  |   safe (default):  close waits until every copy the folder asks for    |
  |                    is COMPLETE -> flushed to disk -> renamed            |
  |                    img.TEMP -> img.raw                                  |
  |                    The copies fill in parallel, one per disk, so       |
  |                    waiting for all of them costs about what waiting    |
  |                    for one would.                                      |
  |                                                                        |
  |   performance:     close returns at once; filling, flushing and        |
  |                    renaming finish in the background.                  |
  |                                                                        |
  |   Helper temps are deleted once no final needs their blocks.           |
  +------------------------------------------------------------------------+
                                        |
                                        v
  +------------------------------------------------------------------------+
  |  5. LATER (landing zone only): the drainer moves settled files down    |
  |     to the storage group, several at a time, each to a different       |
  |     storage disk; the landing copy is freed only once the storage      |
  |     copy is complete and durable.                                      |
  +------------------------------------------------------------------------+
```

## The rules, stated plainly

- **Landing zone first.** If a landing zone is configured, new files go to the landing-zone group;
  otherwise to the storage group. A landing zone is always something the user configures: the
  pool never promotes a disk to landing zone by itself.
- **Doesn't fit, doesn't land.** A file that does not fit the landing zone (past its low watermark,
  or simply too big for the space left) goes straight to the storage group.
- **Outgrowing the landing zone.** A file that grows past what the landing zone can hold *while it
  is being written* is moved to the storage group first, and written further from there. With a
  stripe session that is a re-home: the session's disks become the storage group's, and the blocks
  already written on landing-zone disks are copied across as part of filling.
- **Decided once.** The group and the placement strategy (`most-free-space`, `round-robin`,
  `least-used`, `lowest-latency`) choose the final disks when the file arrives. Nothing re-decides
  them afterwards, except a disk failing during the write, or the landing-zone overflow above.
- **Per block, the session decides.** Between arrival and close, the stripe session sends each block
  to the disk of the group that can take it first, in contiguous pieces of at least 1 MiB: a small
  write stays on one disk, and a large one is split into big pieces rather than scattered block by
  block.
- **Never a half file under the real name.** Everything is written to temp names first. Only a
  complete, flushed temp is renamed to the real name, so a power cut at any moment leaves either the
  whole file or no file under that name. Temps a power cut leaves behind are swept at the next mount.
  This needs disks that can rename atomically, which every local disk can. A pool with a
  whole-file remote member (FTP, WebDAV, cloud storage) writes the real name directly and
  verifies it afterwards, and does not stripe.
- **Every acknowledged block is on as many disks as the ack count asks.** Striping never weakens
  that: with duplication, each block goes to the two readiest disks, on different physical disks.
- **A disk that refuses a write is not a disk that lost data.** It gets no new blocks, but what it
  already holds stays valid. At close every reachable copy is filled; one that cannot be completed
  is dropped and the healer makes it again from the published file. If no copy can be completed,
  the helper holding the most blocks becomes the copy.
- **Idle and read-only disks never take part.** An idle disk only receives files from a disk being
  retired; a read-only disk takes nothing.

## Status

| Part | State |
| --- | --- |
| Landing zone preferred, storage otherwise; landing zone only by user configuration | implemented |
| Placement strategy picks the final disks at arrival | implemented |
| Temp names first; only a complete, flushed temp is renamed | implemented |
| Idle and read-only disks excluded | implemented |
| Stripe session: per-block choice across the whole group, helpers, block map, filling | implemented |
| Safe vs. performance close for striped files | implemented |
| File doesn't fit the landing zone at arrival goes to storage (using the announced size) | implemented |
| File outgrowing the landing zone mid-write is re-homed to storage | implemented |
| Drainer moves several files at once, to different storage disks (`tiers.fast.drainConcurrency`, default 2) | implemented |
