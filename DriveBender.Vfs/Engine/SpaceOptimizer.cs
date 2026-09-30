using System.Security.Cryptography;

namespace DivisonM.Vfs.Engine;

/// <summary>What one optimizing pass saved (docs/SpaceSavings.md).</summary>
/// <param name="SkippedBusy">Files left alone because they were in use when the change was to be made.</param>
/// <param name="SkippedChanged">Files left alone because they changed while the pass worked with them.</param>
public sealed record SpaceReport(int FilesDeduplicated, long BytesDeduplicated, int FilesSparsified, long BytesReleased, int SkippedBusy, int SkippedMetadataDiffers,
  int SkippedChanged = 0) {
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
/// NOTHING IS HELD WHILE IT WORKS. Hashing, comparing and preparing a clone happen while
/// applications keep reading and writing the files; the pass only watches them
/// (<see cref="IChangeFeed"/>). The change itself — one link and rename, or the hole punches — is
/// made in a commit window that is refused, never waited for, when a file changed or is in use; the
/// file is then left for the next pass. Under that window the size and time are compared once more
/// with what the pass read, so a change the watch heard of late is still caught.
/// </summary>
/// <param name="openFeed">Opens the change feed for one pass; disposed at its end when disposable.</param>
/// <param name="admit">Charges a member for the bytes the pass reads, so it keeps to the member's background allowance.</param>
public sealed class SpaceOptimizer(IReadOnlyList<IVolumeIO> members, Func<IChangeFeed> openFeed, bool deduplicate = true, bool sparsify = true,
  Action<IVolumeIO, long>? admit = null) {

  /// <summary>Files smaller than this are not worth a hash, a compare and a rename.</summary>
  public const long MinimumDeduplicateBytes = 64 * 1024;

  /// <summary>The granularity of released ranges: whole, aligned megabytes of zeros.</summary>
  public const int SparseUnit = 1024 * 1024;

  private sealed record Candidate(IVolumeIO Member, string Path, bool Shadow, long Length);

  /// <summary>A candidate as the pass read it, watched from before its first byte was read.</summary>
  private sealed record Seen(Candidate File, IPathWatch Watch, FileMeta Meta);

  public SpaceReport Run(OperationContext? operation = null) {
    operation ??= OperationContext.None;
    var feed = openFeed();
    try {
      var report = SpaceReport.Empty;
      foreach (var member in members.Where(m => m.IsOnline)) {
        operation.ThrowIfStopping();
        var files = _Walk(member).ToList();
        if (deduplicate && (member.Caps & (BackendCaps.HardLinks | BackendCaps.BlockClone)) != 0)
          report = this._Deduplicate(feed, member, files, report, operation);
        if (sparsify && (member.Caps & BackendCaps.Sparse) != 0)
          report = this._Sparsify(feed, files, report, operation);
      }

      return report;
    } finally {
      (feed as IDisposable)?.Dispose();
    }
  }

  #region deduplication

  private SpaceReport _Deduplicate(IChangeFeed feed, IVolumeIO member, List<Candidate> files, SpaceReport report, OperationContext operation) {
    // only files that share a size can share content; hashing is spent on those alone
    foreach (var sameSize in files.Where(f => f.Length >= MinimumDeduplicateBytes).GroupBy(f => f.Length).Where(g => g.Count() > 1)) {
      var seen = new List<Seen>();
      try {
        var byHash = new Dictionary<string, List<Seen>>();
        foreach (var file in sameSize) {
          operation.ThrowIfStopping();
          var watch = feed.Watch(file.Path); // before the first byte is read: every change from here on is heard
          if (_StatOf(file) is not { } meta || this._HashOf(file) is not { } hash || watch.Changed) {
            watch.Dispose();
            continue;
          }

          var entry = new Seen(file, watch, meta);
          seen.Add(entry);
          (byHash.TryGetValue(hash, out var list) ? list : byHash[hash] = []).Add(entry);
        }

        foreach (var group in byHash.Values.Where(g => g.Count > 1)) {
          var keep = group.OrderBy(f => f.File.Path, StringComparer.Ordinal).ThenBy(f => f.File.Shadow).First();
          foreach (var duplicate in group.Where(f => f != keep)) {
            // two copies of one file are never made to share: that would undo its duplication
            if (string.Equals(duplicate.File.Path, keep.File.Path, PoolPaths.PathComparison))
              continue;

            operation.Step(report.FilesDeduplicated, 0, $"deduplicating {duplicate.File.Path} on {member.DisplayName}");
            report = this._Share(feed, member, keep, duplicate, report);
          }
        }
      } finally {
        foreach (var entry in seen)
          entry.Watch.Dispose();
      }
    }

    return report;
  }

  private SpaceReport _Share(IChangeFeed feed, IVolumeIO member, Seen keep, Seen duplicate, SpaceReport report) {
    var changed = report with { SkippedChanged = report.SkippedChanged + 1 };
    if (keep.Watch.Changed || duplicate.Watch.Changed)
      return changed;

    // byte for byte, and without holding either file: a hash only chose the pair
    if (!this._SameBytes(member, keep.File, duplicate.File))
      return keep.Watch.Changed || duplicate.Watch.Changed ? changed : report;

    var kept = keep.Meta;
    var dup = duplicate.Meta;
    var shadow = duplicate.File.Shadow;
    var temp = $"{duplicate.File.Path}.{Guid.NewGuid().ToString("N")[..8]}.DEDUP.{DriveBender.DriveBenderConstants.TEMP_EXTENSION}";
    var committed = false;
    try {
      var cloned = (member.Caps & BackendCaps.BlockClone) != 0 && member.TryClone(keep.File.Path, keep.File.Shadow, temp, shadow);
      if (cloned) {
        // a clone is verified before it replaces anything: a filesystem call that went wrong costs a temp, not a file
        if (!this._SameBytes(member, keep.File, duplicate.File with { Path = temp }))
          return report;

        if (dup.Permissions is { } mode && (member.Caps & BackendCaps.Permissions) != 0)
          member.SetPermissions(temp, shadow, mode);
        member.SetTimestamps(temp, shadow, dup.CreationTimeUtc, dup.LastWriteTimeUtc); // the clone keeps the duplicate's own times
      } else if ((member.Caps & BackendCaps.HardLinks) == 0)
        return report;
      else if (kept.LastWriteTimeUtc != dup.LastWriteTimeUtc || kept.CreationTimeUtc != dup.CreationTimeUtc
               || (kept.Attributes & ~FileAttributes.Archive) != (dup.Attributes & ~FileAttributes.Archive) || kept.Permissions != dup.Permissions)
        return report with { SkippedMetadataDiffers = report.SkippedMetadataDiffers + 1 }; // linked names share their metadata

      // the only moment either file is held: one link and one rename, or nothing at all
      using (var window = feed.TryCommit([keep.Watch, duplicate.Watch])) {
        if (window == null)
          return keep.Watch.Changed || duplicate.Watch.Changed ? changed : report with { SkippedBusy = report.SkippedBusy + 1 };
        if (!_AsSeen(keep) || !_AsSeen(duplicate))
          return changed;
        if (!cloned && !member.TryHardLink(keep.File.Path, keep.File.Shadow, temp, shadow))
          return report;

        member.AtomicReplace(temp, duplicate.File.Path, shadow);
        committed = true;
      }

      DriveBender.Logger($" - Deduplicated '{duplicate.File.Path}' with '{keep.File.Path}' on '{member.DisplayName}' ({dup.Length:N0} bytes)");
      return report with { FilesDeduplicated = report.FilesDeduplicated + 1, BytesDeduplicated = report.BytesDeduplicated + dup.Length };
    } catch (PoolFsException e) {
      DriveBender.Logger($"[Warning]Could not deduplicate '{duplicate.File.Path}' on '{member.DisplayName}': {e.Message}");
      return report;
    } finally {
      if (!committed)
        _Discard(member, temp, shadow);
    }
  }

  private string? _HashOf(Candidate file) {
    try {
      using var stream = file.Member.OpenRead(file.Path, file.Shadow);
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var buffer = new byte[SparseUnit];
      while (this._Read(file.Member, stream, buffer) is var read and > 0)
        hash.AppendData(buffer, 0, read);

      return Convert.ToHexString(hash.GetHashAndReset());
    } catch (PoolFsException) {
      return null; // unreadable right now: not a candidate this pass
    }
  }

  private bool _SameBytes(IVolumeIO member, Candidate a, Candidate b) {
    try {
      using var left = member.OpenRead(a.Path, a.Shadow);
      using var right = member.OpenRead(b.Path, b.Shadow);
      var bufferA = new byte[SparseUnit];
      var bufferB = new byte[SparseUnit];
      while (true) {
        var readA = this._Read(member, left, bufferA);
        var readB = this._Read(member, right, bufferB);
        if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
          return false;
        if (readA == 0)
          return true;
      }
    } catch (PoolFsException) {
      return false;
    }
  }

  #endregion

  #region sparse

  private SpaceReport _Sparsify(IChangeFeed feed, List<Candidate> files, SpaceReport report, OperationContext operation) {
    foreach (var file in files.Where(f => f.Length >= SparseUnit * 2L)) {
      operation.ThrowIfStopping();
      using var watch = feed.Watch(file.Path); // before the scan: a write landing in a run from here on is heard
      if (_StatOf(file) is not { } before)
        continue;

      var runs = this._ZeroRuns(file);
      if (runs.Count == 0)
        continue;

      var member = file.Member;
      var allocatedBefore = member.AllocatedBytes(file.Path, file.Shadow);
      long released = 0;
      try {
        using var window = feed.TryCommit([watch]);
        if (window == null) {
          report = watch.Changed ? report with { SkippedChanged = report.SkippedChanged + 1 } : report with { SkippedBusy = report.SkippedBusy + 1 };
          continue;
        }

        // unchanged since the scan, and nothing can write now: the runs are still zeros
        if (!_AsSeen(new(file, watch, before))) {
          report = report with { SkippedChanged = report.SkippedChanged + 1 };
          continue;
        }

        foreach (var (offset, length) in runs)
          if (member.TryPunchHole(file.Path, file.Shadow, offset, length))
            released += length;

        // releasing a range counts as a write to the filesystem; the file's own times are put back
        if (released > 0)
          member.SetTimestamps(file.Path, file.Shadow, before.CreationTimeUtc, before.LastWriteTimeUtc);
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Could not release zeros in '{file.Path}' on '{member.DisplayName}': {e.Message}");
        continue;
      }

      if (released == 0)
        continue;

      var allocatedAfter = member.AllocatedBytes(file.Path, file.Shadow);
      var saved = allocatedBefore >= 0 && allocatedAfter >= 0 ? Math.Max(0, allocatedBefore - allocatedAfter) : released;
      if (saved > 0)
        report = report with { FilesSparsified = report.FilesSparsified + 1, BytesReleased = report.BytesReleased + saved };
    }

    return report;
  }

  /// <summary>Aligned runs of whole zero megabytes: (offset, length).</summary>
  private List<(long Offset, long Length)> _ZeroRuns(Candidate file) {
    var runs = new List<(long, long)>();
    try {
      using var stream = file.Member.OpenRead(file.Path, file.Shadow);
      var buffer = new byte[SparseUnit];
      long offset = 0;
      long? runStart = null;
      while (true) {
        var read = this._Read(file.Member, stream, buffer);
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

  /// <summary>Whether a file still has the size and time the pass read: the last word, under the commit window.</summary>
  private static bool _AsSeen(Seen entry)
    => _StatOf(entry.File) is { } now && now.Length == entry.Meta.Length && now.LastWriteTimeUtc == entry.Meta.LastWriteTimeUtc;

  private static FileMeta? _StatOf(Candidate file) {
    try {
      return file.Member.Stat(file.Path, file.Shadow);
    } catch (PoolFsException) {
      return null;
    }
  }

  private static void _Discard(IVolumeIO member, string temp, bool shadow) {
    try {
      if (member.FileExists(temp, shadow))
        member.Delete(temp, shadow);
    } catch (PoolFsException) {
      // an orphaned temp is swept on the next mount
    }
  }

  /// <summary>Fills the buffer as far as the stream allows, charged to the member's background allowance.</summary>
  private int _Read(IVolumeIO member, Stream stream, byte[] buffer) {
    admit?.Invoke(member, buffer.Length);
    var total = 0;
    while (total < buffer.Length && stream.Read(buffer, total, buffer.Length - total) is var got and > 0)
      total += got;

    return total;
  }

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
