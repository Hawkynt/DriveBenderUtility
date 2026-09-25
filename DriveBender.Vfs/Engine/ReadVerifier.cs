using System.Collections.Concurrent;

namespace DivisonM.Vfs.Engine;

/// <summary>What checking one copy against its recorded checksum concluded.</summary>
public enum CopyVerdict {
  /// <summary>The copy's content matches the checksum recorded for this exact version of it.</summary>
  Intact,

  /// <summary>Same size and mtime as recorded, different content: bit-rot by definition.</summary>
  Damaged,

  /// <summary>No baseline that still describes this copy — never recorded, or changed since. Nothing to judge it by.</summary>
  Unverifiable,
}

/// <summary>
/// Checks reads against the checksum database per <see cref="ReadVerification"/> (<c>integrity.verifyReads</c>).
///
/// The database holds WHOLE-FILE hashes and a read serves a block, so a block cannot be checked on
/// its own. What is checked is the COPY that serves it, once per version of that copy: a verdict is
/// remembered against (member, copy, size, mtime), so the first read after a file changes pays for
/// one streamed hash and every later read of the same version pays nothing. A repaired or rewritten
/// copy has a new mtime, and so is checked afresh.
/// </summary>
public sealed class ReadVerifier(IntegrityService integrity, Action<string> invalidateCaches, Action<string> repair) {

  private readonly record struct CopyVersion(Guid Member, bool Shadow, long Size, long MTimeTicks);

  /// <summary>Bounded by clearing: a verdict is only an optimisation, so losing them all costs one hash each.</summary>
  private const int _MAX_PATHS = 100_000;

  // per PATH, so everything known about a file can be forgotten in one step when it changes
  private readonly ConcurrentDictionary<string, ConcurrentDictionary<CopyVersion, CopyVerdict>> _verdicts = new(PoolPaths.PathComparer);
  private readonly ConcurrentDictionary<string, byte> _checking = new(PoolPaths.PathComparer);
  private long _hashes;
  private int _pending;

  public ReadVerification Mode { get; set; } = ReadVerification.Never;

  /// <summary>How many copies have been hashed to reach a verdict — the cost this feature adds.</summary>
  public long Hashes => Interlocked.Read(ref this._hashes);

  /// <summary>
  /// <see cref="ReadVerification.Before"/>: the copies a read may be served from.
  ///
  /// Copies are checked in the order they would serve, and checking stops at the first intact one,
  /// so a healthy file costs one hash rather than one per copy. That copy alone is returned: serving
  /// the rest would hand out bytes nobody checked. With no intact copy, the unverifiable ones are
  /// returned — refusing a file the database simply never saw would make this setting unusable on a
  /// pool that has not been scrubbed yet. With every copy damaged the read FAILS, because the whole
  /// point of this mode is that damage is never handed over.
  /// </summary>
  public IReadOnlyList<PhysicalCopy> SelectForRead(string path, IReadOnlyList<PhysicalCopy> copies) {
    var unverifiable = new List<PhysicalCopy>();
    var damaged = new List<PhysicalCopy>();
    PhysicalCopy? intact = null;
    foreach (var copy in copies) {
      switch (this._Verdict(path, copy)) {
        case CopyVerdict.Intact:
          intact = copy;
          break;
        case CopyVerdict.Damaged:
          damaged.Add(copy);
          continue;
        default:
          unverifiable.Add(copy);
          continue;
      }

      break;
    }

    if (damaged.Count > 0) {
      // the block cache may hold bytes loaded from the damaged copy before anyone looked
      invalidateCaches(path);
      var names = string.Join(", ", damaged.Select(c => $"'{c.Volume.DisplayName}'"));
      DriveBender.Logger(intact != null || unverifiable.Count > 0
        ? $"[Warning]Read check: '{path}' does not match its stored checksum on {names} — served from another copy instead; repairing the damaged one"
        : $"[Warning]Read check: '{path}' does not match its stored checksum on ANY copy ({names}) — the read was refused rather than hand over damaged data");
      this._QueueRepair(path);
    }

    if (intact != null)
      return [intact];

    if (unverifiable.Count > 0)
      return unverifiable;

    throw new PoolFsException(PoolFsError.IoError, $"No copy of '{path}' matches its stored checksum");
  }

  /// <summary>
  /// <see cref="ReadVerification.After"/>: the read has already been served; check every copy in the
  /// background and, where one is damaged, say so and repair it. One check per file at a time —
  /// a file read in a tight loop queues one check, not one per read.
  /// </summary>
  public void CheckAfterRead(string path, IReadOnlyList<PhysicalCopy> copies) {
    if (!this._checking.TryAdd(path, 0))
      return;

    Interlocked.Increment(ref this._pending);
    ThreadPool.QueueUserWorkItem(state => {
      try {
        var damaged = copies.Where(c => this._Verdict(path, c) == CopyVerdict.Damaged).ToArray();
        if (damaged.Length == 0)
          return;

        invalidateCaches(path);
        var names = string.Join(", ", damaged.Select(c => $"'{c.Volume.DisplayName}'"));
        DriveBender.Logger($"[Warning]Read check: '{path}' was already handed to a reader, and its copy on {names} "
                           + "does not match its stored checksum — that reader may have received damaged data; repairing it");
        this._QueueRepair(path);
      } catch (Exception e) {
        DriveBender.Logger($"[Warning]Read check of '{path}' could not run: {e.Message}");
      } finally {
        this._checking.TryRemove(path, out _);
        Interlocked.Decrement(ref this._pending);
      }
    });
  }

  /// <summary>
  /// Drops every verdict about a path. Size and mtime alone do not prove a copy unchanged: a heal
  /// deliberately PRESERVES the mtime, so a repaired copy looks exactly like the damaged one it
  /// replaced, and a copy tool can stamp a rewritten file back to its old time. Anything the pool
  /// itself does to a file therefore forgets what was concluded about it.
  /// </summary>
  public void Forget(string path) => this._verdicts.TryRemove(path, out _);

  /// <summary>Waits until every queued check and repair has finished; false on timeout.</summary>
  public bool WaitIdle(TimeSpan timeout) {
    var deadline = DateTime.UtcNow + timeout;
    while (Volatile.Read(ref this._pending) > 0) {
      if (DateTime.UtcNow >= deadline)
        return false;

      Thread.Sleep(10);
    }

    return true;
  }

  private void _QueueRepair(string path) {
    Interlocked.Increment(ref this._pending);
    ThreadPool.QueueUserWorkItem(state => {
      try {
        repair(path);
      } catch (Exception e) {
        DriveBender.Logger($"[Warning]Repairing '{path}' after a failed read check did not complete: {e.Message}; the scrub will retry it");
      } finally {
        // a heal preserves the mtime, so the repaired copy would otherwise keep its "damaged" verdict
        this.Forget(path);
        Interlocked.Decrement(ref this._pending);
      }
    });
  }

  private CopyVerdict _Verdict(string path, PhysicalCopy copy) {
    FileMeta? stat;
    try {
      stat = copy.Volume.Stat(path, copy.Shadow);
    } catch (PoolFsException) {
      return CopyVerdict.Unverifiable; // unreachable right now; the read path reports that itself
    }

    if (stat is not { } meta || meta.IsDirectory)
      return CopyVerdict.Unverifiable;

    var version = new CopyVersion(copy.Volume.MemberId, copy.Shadow, meta.Length, meta.LastWriteTimeUtc.Ticks);
    if (this._verdicts.TryGetValue(path, out var known) && known.TryGetValue(version, out var verdict))
      return verdict;

    verdict = this._Judge(path, copy, meta);
    if (this._verdicts.Count >= _MAX_PATHS)
      this._verdicts.Clear();

    this._verdicts.GetOrAdd(path, _ => new())[version] = verdict;
    return verdict;
  }

  private CopyVerdict _Judge(string path, PhysicalCopy copy, FileMeta meta) {
    var baseline = integrity.TrustedBaseline(copy.Volume, path, copy.Shadow, meta);
    if (baseline == null)
      return CopyVerdict.Unverifiable;

    string hash;
    try {
      using var stream = copy.Volume.OpenRead(path, copy.Shadow);
      hash = ChecksumDatabase.HashOf(stream); // streamed — a multi-GB copy is never held in RAM
    } catch (PoolFsException) {
      return CopyVerdict.Unverifiable;
    }

    Interlocked.Increment(ref this._hashes);
    return hash == baseline.Hash ? CopyVerdict.Intact : CopyVerdict.Damaged;
  }

}
