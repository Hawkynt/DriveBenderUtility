# Space savings: shared data and released zeros

A pool keeps whole files on ordinary filesystems, so two identical files take their space twice, and
a disk image full of zeros takes all of it. Where a member's filesystem can do better, the health
scan gives that space back. **What any file holds never changes**, and neither does what the pool
shows.

Two jobs, both on by default:

| Job | What it does | Needs, on the member's filesystem |
| --- | --- | --- |
| **Deduplicate** | Two *different* files with identical content on one disk come to share their data. | block cloning (ReFS, Btrfs, XFS), or hard links (NTFS, ext4, ...) |
| **Sparsify** | Long runs of zeros inside a file are released to the filesystem. | sparse files (NTFS, ReFS, ext4, Btrfs, XFS) |

The mount probes each member once for what its filesystem supports. A member that supports neither
(FAT, exFAT, every remote backend) is left alone.

## When it runs

```
  mounted pool                              pool health --fix
  ------------                              -----------------
  scheduled quick / deep scrub              repairs: bit rot, stale copies,
          |                                 conflicts, missing copies
          v                                         |
  +--------------------------------------------------------------+
  |  space optimizer, one member at a time                        |
  |                                                               |
  |   1. deduplicate   same size -> SHA-256 -> byte compare       |
  |   2. sparsify      aligned 1 MiB runs of zeros                |
  +--------------------------------------------------------------+
          |
          v
  "Space saved" in the report / log / dashboard
```

It runs after the repairs, because only healthy, whole files are worth sharing. A plain
`pool health` (without `--fix`) only looks; it never changes a pool, and it does not save space
either.

```jsonc
{
  "space": {
    "deduplicate": true,   // share identical files' data on each disk
    "sparsify": true       // release long runs of zeros
  }
}
```

## Deduplication

Only files on the **same disk** can share data, so each member is handled on its own:

1. Files of at least 64 KiB are grouped by size; only a size shared by two or more files is hashed.
2. Files with the same SHA-256 are a candidate pair. The first path in ordinal order is kept.
3. Both files are locked, in a fixed order so two passes cannot deadlock. A file that is open, still
   being written, dirty in the write buffer or staged is **skipped** this pass and counted as busy.
4. Under the locks the two files are **compared byte for byte**. A hash only chooses the pair.
5. The shared file is made as a temp (`<name>.<token>.DEDUP.TEMP.$DRIVEBENDER`) and renamed over the
   duplicate atomically, so a power cut leaves the old file, or the shared one, and at worst a temp
   that the next mount sweeps.

How the data is shared depends on the filesystem:

- **Block clone** (preferred). The duplicate becomes a clone of the kept file: it shares the blocks
  but keeps its own times, attributes and mode, which are put back on the clone. The clone is
  compared again before it replaces anything. A later write to either file gets its own copies of
  the blocks it changes; the filesystem does this.
- **Hard link.** Two names for one file share their times, attributes and mode as well as the data.
  So a hard link is made **only when those already match**: same modification and creation time,
  same attributes (the archive bit aside), same mode. Otherwise the pair is skipped and counted as
  "metadata differs". Linking must never change a file's visible metadata.

**Two copies of the same file are never paired.** When a folder's duplication level puts a file's
primary and its shadow copy on one disk, they are identical by design; sharing their data would
silently undo the duplication. Only different pool paths are ever paired.

## Copy-on-write for hard links

A block clone separates by itself on a write. A hard link does not: a write into one name is a
write into both. So before the engine changes a file in place through the pool, it checks the
file's link count, and a file that shares its data gets data of its own first:

```
  write / truncate / new times or attributes to "backup/copy.raw"
                          |
                          v
          link count on this disk > 1 ?  --no-->  change it in place
                          | yes
                          v
      copy it to a temp (content, times, attributes, mode),
      flush, rename the temp over "backup/copy.raw"
                          |
                          v
      "backup/copy.raw" now has its own data; "photos/original.raw"
      still has the old one, untouched  -->  change it in place
```

This happens once per open file, on its first change. It also covers the paths that write in place
without an ordinary write: a copy the write buffer still owes to a second disk, and a journal replay
after a power cut. A power cut in the middle leaves the shared file intact, because the temp is
renamed only when it is whole.

## Sparsifying

Files of at least 2 MiB are read in aligned 1 MiB units. Every run of whole zero units is released
to the filesystem (`FSCTL_SET_ZERO_DATA` on a sparse file on Windows, `fallocate` with
`PUNCH_HOLE | KEEP_SIZE` on Linux). The file keeps its size and reads back the same zeros. The
filesystem counts the release as a write, so the file's times are put back afterwards. The saving
reported is what the filesystem actually allocates less, not the size of the runs.

## Limits

- **Outside the pool, links are still links.** Copy-on-write is the engine's job. A program that
  writes straight into a member's folder, bypassing the pool, writes into both names of a hard link.
  Do not edit member disks directly; a block-cloned file is unaffected.
- **Bit rot in shared data reaches every name that shares it.** Each such file still has its
  copies on other disks, and the scrub heals the damaged ones from those. With duplication 1 there is
  no other copy, as always.
- **A file that changes often un-shares again.** Its first write through the pool gives it its own
  data, and the next health scan may share it again if it is identical once more.
- Files smaller than 64 KiB are not deduplicated: a hash, a compare and a rename would cost more
  than they save.
