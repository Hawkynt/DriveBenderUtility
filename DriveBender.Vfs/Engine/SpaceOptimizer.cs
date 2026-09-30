using System.Security.Cryptography;

namespace DivisonM.Vfs.Engine;

/// <summary>What one optimizing pass saved (docs/SpaceSavings.md).</summary>
public sealed record SpaceReport(int FilesDeduplicated, long BytesDeduplicated, int FilesSparsified, long BytesReleased, int SkippedBusy, int SkippedMetadataDiffers) {
  public static readonly SpaceReport Empty = new(0, 0, 0, 0, 0, 0);
  public long BytesSaved => this.BytesDeduplicated + this.BytesReleased;
}

/// <summary>
/// Saves space on the disks of a pool without changing what any file holds (docs/SpaceSavings.md).
///
/// DEDUPLICATION, within one disk: two different files with identical content come to share their
/// data. By block cloning where the filesystem can (each file keeps its own metadata, and writing
/// either gets copies of the blocks it changes), otherwise by a hard link — and a hard link only when
/// the two files' times, attributes and mode already match, because linked names share those too.
/// The engine gives a linked file data of its own before any in-place change (copy-on-write), so a
/// write to one never reaches the other. Two copies of the SAME file are never paired: sharing them
/// would silently undo the duplication the folder asked for.
///
/// SPARSIFYING: long runs of zeros are released to the filesystem. The content and size stay, and
/// the file's times are put back afterwards.
///
/// Every change is made under the file's lock, after comparing the bytes themselves — never on a
/// hash alone — and lands by an atomic rename of a temp, so a power cut leaves either the old file
/// or the new name, and at worst a temp that recovery sweeps.
/// </summary>
/// <param name="lockPath">Locks a pool path for the change, or returns null when it is busy (then it is skipped).</param>
public sealed class SpaceOptimizer(IReadOnlyList<IVolumeIO> members, Func<string, IDisposable?> lockPath, bool deduplicate = true, bool sparsify = true) {

  /// <summary>Files smaller than this are not worth a hash, a compare and a rename.</summary>
  public const long MinimumDeduplicateBytes = 64 * 1024;

  /// <summary>The granularity of released ranges: whole, aligned megabytes of zeros.</summary>
  public const int SparseUnit = 1024 * 1024;

  private sealed record Candidate(IVolumeIO Member, string Path, bool Shadow, long Length);

  public SpaceReport Run(OperationContext? operation = null) {
    operation ??= OperationContext.None;
    var report = SpaceReport.Empty;
    foreach (var member in members.Where(m => m.IsOnline)) {
      operation.ThrowIfStopping();
      var files = _Walk(member).ToList();
      if (deduplicate && (member.Caps & (BackendCaps.HardLinks | BackendCaps.BlockClone)) != 0)
        report = this._Deduplicate(member, files, report, operation);
      if (sparsify && (member.Caps & BackendCaps.Sparse) != 0)
        report = this._Sparsify(files, report, operation);
    }

    return report;
  }

  #region deduplication

  private SpaceReport _Deduplicate(IVolumeIO member, List<Candidate> files, SpaceReport report, OperationContext operation) {
    // only files that share a size can share content; hashing is spent on those alone
    foreach (var sameSize in files.Where(f => f.Length >= MinimumDeduplicateBytes).GroupBy(f => f.Length).Where(g => g.Count() > 1)) {
      var byHash = new Dictionary<string, List<Candidate>>();
      foreach (var file in sameSize) {
        operation.ThrowIfStopping();
        if (_HashOf(file) is { } hash)
          (byHash.TryGetValue(hash, out var list) ? list : byHash[hash] = []).Add(file);
      }

      foreach (var group in byHash.Values.Where(g => g.Count > 1)) {
        var keep = group.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Shadow).First();
        foreach (var duplicate in group.Where(f => f != keep)) {
          // two copies of one file are never made to share: that would undo its duplication
          if (string.Equals(duplicate.Path, keep.Path, PoolPaths.PathComparison))
            continue;

          operation.Step(report.FilesDeduplicated, 0, $"deduplicating {duplicate.Path} on {member.DisplayName}");
          report = this._Share(member, keep, duplicate, report);
        }
      }
    }

    return report;
  }

  private SpaceReport _Share(IVolumeIO member, Candidate keep, Candidate duplicate, SpaceReport report) {
    // both files locked, in one fixed order so two passes can never deadlock
    var first = string.CompareOrdinal(keep.Path, duplicate.Path) <= 0 ? keep.Path : duplicate.Path;
    var second = first == keep.Path ? duplicate.Path : keep.Path;
    using var firstLock = lockPath(first);
    using var secondLock = firstLock == null ? null : lockPath(second);
    if (firstLock == null || secondLock == null)
      return report with { SkippedBusy = report.SkippedBusy + 1 };

    // re-checked under the locks, byte for byte — a hash only chose the pair
    var keepMeta = member.Stat(keep.Path, keep.Shadow);
    var duplicateMeta = member.Stat(duplicate.Path, duplicate.Shadow);
    if (keepMeta is not { } kept || duplicateMeta is not { } dup || kept.Length != dup.Length || !_SameBytes(member, keep, duplicate))
      return report;

    var temp = $"{duplicate.Path}.{Guid.NewGuid().ToString("N")[..8]}.DEDUP.{DriveBender.DriveBenderConstants.TEMP_EXTENSION}";
    try {
      if ((member.Caps & BackendCaps.BlockClone) != 0 && member.TryClone(keep.Path, keep.Shadow, temp, duplicate.Shadow)) {
        // a clone is verified before it replaces anything: a filesystem call that went wrong costs a temp, not a file
        if (!_SameBytes(member, keep, duplicate with { Path = temp })) {
          member.Delete(temp, duplicate.Shadow);
          return report;
        }

        if (dup.Permissions is { } mode && (member.Caps & BackendCaps.Permissions) != 0)
          member.SetPermissions(temp, duplicate.Shadow, mode);
        member.SetTimestamps(temp, duplicate.Shadow, dup.CreationTimeUtc, dup.LastWriteTimeUtc); // the clone keeps the duplicate's own times
      } else if ((member.Caps & BackendCaps.HardLinks) != 0) {
        // linked names share their metadata, so a link is made only where it is already the same
        if (kept.LastWriteTimeUtc != dup.LastWriteTimeUtc || kept.CreationTimeUtc != dup.CreationTimeUtc
            || (kept.Attributes & ~FileAttributes.Archive) != (dup.Attributes & ~FileAttributes.Archive) || kept.Permissions != dup.Permissions)
          return report with { SkippedMetadataDiffers = report.SkippedMetadataDiffers + 1 };

        if (!member.TryHardLink(keep.Path, keep.Shadow, temp, duplicate.Shadow))
          return report;
      } else
        return report;

      member.AtomicReplace(temp, duplicate.Path, duplicate.Shadow);
      DriveBender.Logger($" - Deduplicated '{duplicate.Path}' with '{keep.Path}' on '{member.DisplayName}' ({dup.Length:N0} bytes)");
      return report with { FilesDeduplicated = report.FilesDeduplicated + 1, BytesDeduplicated = report.BytesDeduplicated + dup.Length };
    } catch (PoolFsException e) {
      try {
        if (member.FileExists(temp, duplicate.Shadow))
          member.Delete(temp, duplicate.Shadow);
      } catch (PoolFsException) {
        // an orphaned temp is swept on the next mount
      }

      DriveBender.Logger($"[Warning]Could not deduplicate '{duplicate.Path}' on '{member.DisplayName}': {e.Message}");
      return report;
    }
  }

  private static string? _HashOf(Candidate file) {
    try {
      using var stream = file.Member.OpenRead(file.Path, file.Shadow);
      return Convert.ToHexString(SHA256.HashData(stream));
    } catch (PoolFsException) {
      return null; // unreadable right now: not a candidate this pass
    }
  }

  private static bool _SameBytes(IVolumeIO member, Candidate a, Candidate b) {
    using var left = member.OpenRead(a.Path, a.Shadow);
    using var right = member.OpenRead(b.Path, b.Shadow);
    var bufferA = new byte[1024 * 1024];
    var bufferB = new byte[1024 * 1024];
    while (true) {
      var readA = _Fill(left, bufferA);
      var readB = _Fill(right, bufferB);
      if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
        return false;
      if (readA == 0)
        return true;
    }
  }

  private static int _Fill(Stream stream, byte[] buffer) {
    var total = 0;
    while (total < buffer.Length && stream.Read(buffer, total, buffer.Length - total) is var got and > 0)
      total += got;

    return total;
  }

  #endregion

  #region sparse

  private SpaceReport _Sparsify(List<Candidate> files, SpaceReport report, OperationContext operation) {
    foreach (var file in files.Where(f => f.Length >= SparseUnit * 2L)) {
      operation.ThrowIfStopping();
      var runs = _ZeroRuns(file);
      if (runs.Count == 0)
        continue;

      using var locked = lockPath(file.Path);
      if (locked == null) {
        report = report with { SkippedBusy = report.SkippedBusy + 1 };
        continue;
      }

      var member = file.Member;
      var before = member.Stat(file.Path, file.Shadow);
      var allocatedBefore = member.AllocatedBytes(file.Path, file.Shadow);
      runs = _ZeroRuns(file); // again, under the lock
      long released = 0;
      foreach (var (offset, length) in runs)
        if (member.TryPunchHole(file.Path, file.Shadow, offset, length))
          released += length;

      if (released == 0)
        continue;

      // releasing a range counts as a write to the filesystem; the file's own times are put back
      if (before is { } meta)
        member.SetTimestamps(file.Path, file.Shadow, meta.CreationTimeUtc, meta.LastWriteTimeUtc);

      var allocatedAfter = member.AllocatedBytes(file.Path, file.Shadow);
      var saved = allocatedBefore >= 0 && allocatedAfter >= 0 ? Math.Max(0, allocatedBefore - allocatedAfter) : released;
      if (saved > 0)
        report = report with { FilesSparsified = report.FilesSparsified + 1, BytesReleased = report.BytesReleased + saved };
    }

    return report;
  }

  /// <summary>Aligned runs of whole zero megabytes: (offset, length).</summary>
  private static List<(long Offset, long Length)> _ZeroRuns(Candidate file) {
    var runs = new List<(long, long)>();
    try {
      using var stream = file.Member.OpenRead(file.Path, file.Shadow);
      var buffer = new byte[SparseUnit];
      long offset = 0;
      long? runStart = null;
      while (true) {
        var read = _Fill(stream, buffer);
        var zero = read == SparseUnit && !buffer.AsSpan().ContainsAnyExcept((byte)0);
        if (zero)
          runStart ??= offset;
        else if (runStart is { } start) {
          runs.Add((start, offset - start));
          runStart = null;
        }

        if (read < SparseUnit)
          break;

        offset += read;
      }

      if (runStart is { } open)
        runs.Add((open, offset - open));
    } catch (PoolFsException) {
      runs.Clear();
    }

    return runs;
  }

  #endregion

  /// <summary>Every visible file on the member, primary and shadow; the pool's own tree and temps are not candidates.</summary>
  private static IEnumerable<Candidate> _Walk(IVolumeIO member) {
    var stack = new Stack<string>();
    stack.Push("");
    while (stack.Count > 0) {
      var folder = stack.Pop();
      foreach (var shadow in new[] { false, true }) {
        VolumeEntry[] entries;
        try {
          entries = !shadow || member.FolderExists(folder, true) ? [.. member.List(folder, shadow)] : [];
        } catch (PoolFsException) {
          continue;
        }

        foreach (var entry in entries) {
          if (PoolPaths.IsHiddenName(entry.Name))
            continue;

          var path = folder.Length == 0 ? entry.Name : $"{folder}/{entry.Name}";
          if (entry.IsDirectory) {
            if (!shadow)
              stack.Push(path);
          } else
            yield return new(member, path, shadow, entry.Length);
        }
      }
    }
  }

}
