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
   Each file is watched from before its first byte is read (see below).
2. Files with the same SHA-256 are a candidate pair. The first path in ordinal order is kept.
3. The two files are **compared byte for byte**. A hash only chooses the pair.
4. A block clone is prepared and verified as a temp (`<name>.<token>.DEDUP.TEMP.$DRIVEBENDER`).
5. In a short commit window, a hard link is made as that temp if cloning is not available, and the
   temp is renamed over the duplicate atomically. A power cut leaves the old file, or the shared one,
   and at worst a temp that the next mount sweeps.

Steps 1 to 4 hold nothing: applications keep reading and writing both files meanwhile.

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

## Working alongside applications

The pass never makes an application wait on it. It does not lock a file while it hashes, compares
or scans for zeros. It watches the file instead, and acts only if nothing changed:

```
  watch the file ----> read it, hash it, compare it, prepare a clone      (nothing held)
       |                                   |
       |  hears every change:              v
       |   - from the pool engine:    commit window: take the file's lease WITHOUT waiting
       |     writes, truncates,            |
       |     renames, deletes,             +-- lease taken by someone, file open, still being
       |     new times, owed copies        |   written, dirty in the write buffer  --> skip (busy)
       |     landing                       +-- a change was heard                  --> skip (changed)
       |   - from the disk itself:         +-- size or time differs from what      --> skip (changed)
       |     a filesystem watcher on       |   was read
       |     each member                   v
       +------------------------------ link / rename / punch holes, release   (milliseconds)
```

- **The pool engine** reports every change it makes to a watched path the moment the bytes have
  landed. That includes new times and a second copy that the write buffer writes later. Inside a
  mounted pool this is the authoritative source.
- **The disk itself.** During a pass, a filesystem watcher on each member reports changes made by
  anything, including a program writing to a member folder behind the pool's back. That is all
  `pool health --fix` has, since it runs with the pool unmounted. Reads are not reported, so the
  pass's own hashing does not flag its own files.
- **The last word.** Filesystem notifications can come late or overflow, so under the commit window
  the file's size and modification time are compared once more with what the pass read.

The commit window takes the file's lease without waiting, and only when the file is closed, clean and
not staged. A pending folder rename also wins. The window is held for one link and one rename, or for
the hole punches, and then released. A file that is open, busy or changed is simply left for the next
health scan: the report counts it as skipped, never as an error. The pass also reads through each
member's background allowance, the same budget the healer and the drainer use, so it does not take
bandwidth an application needs.

## Copy-on-write for hard links

**Isn't the pool copy-on-write already?** For most writes, yes. A new file, and a save that replaces
a whole file (`File.WriteAllBytes`, an editor saving through a temp, a copy into the pool), is written
to a staged temp and renamed into place. The name then points at new data, and a hard link breaks by
itself at no cost. Snapshots (`docs/Snapshots.md`) protect an older *version* the same way: a file a
snapshot pins is renamed into the snapshot store when it is replaced, and copied there — cloned, on
a member that can clone blocks — before it is changed in place. The optimizer never walks the pool's
own `.drivebenderutility` tree, so nothing in the snapshot store or the recycle bin is ever linked,
cloned or sparsified; and a kept version that still shares data with a live file (a link made before
the file was set aside) is left alone by any change to that file, which is separated first. What neither covers is an **in-place edit** of a file that shares its
data with a *different* file: a database, a VM image, an append. Those write straight into the file
where it lies, and that is where a hard link would carry the write into the other name. So the
engine separates a linked file before an in-place change, and only then.

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

This happens once per open file, on its first change. A truncate copies only what survives the cut,
so emptying a shared file (what an overwrite does first) copies nothing at all. It also covers the paths that write in place
without an ordinary write: a copy the write buffer still owes to a second disk, and a journal replay
after a power cut. A power cut in the middle leaves the shared file intact, because the temp is
renamed only when it is whole.

## Sparsifying

Files of at least 2 MiB are read in aligned 1 MiB units, without holding the file. The runs found are
released in the commit window, only if the file did not change since the scan. Every run of whole zero units is released
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
