using DivisonM.Vfs.Caching;

namespace DivisonM.Vfs.Engine;

/// <summary>Write-policy state of a dirty file (§6.8): RamBuffered → Landed → Draining → Replicated → Clean.</summary>
public enum DirtyState {
  Clean,
  RamBuffered,
  Landed,
  Draining,
  Replicated,
}

/// <summary>One coalescable pending mutation: a positional write or a truncate.</summary>
public sealed record PendingOp(long Offset, byte[]? Data, long? TruncateLength, DateTime StagedUtc) {
  public long End => this.TruncateLength ?? this.Offset + (this.Data?.LongLength ?? 0);
}

/// <summary>
/// The RAM (T0) write buffer (CMP-WP): holds mutations owed to lagging copies (write-back
/// / deferred) or, under performance mode with volatile ack, the only image of the data.
/// Budget comes from the cache instance's write reservation (FR-BACKP: a failed
/// reservation degrades the caller to synchronous durable writes, never unbounded RAM).
/// The buffer is authoritative until flushed (SAFE-COHERE).
/// </summary>
public sealed class WriteBufferManager(CacheInstance cache, Func<DateTime>? clock = null) {

  public sealed class FileBuffer {
    internal readonly List<PendingOp> Ops = [];
    internal readonly List<long> OpenJournalSequences = [];
    internal long ReservedBytes;
    internal int DurableCopies;
    public DirtyState State { get; internal set; } = DirtyState.Clean;
    public DateTime FirstStagedUtc { get; internal set; }
  }

  private readonly Dictionary<string, FileBuffer> _files = new(StringComparer.OrdinalIgnoreCase);
  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
  private readonly Lock _lock = new();

  public IReadOnlyList<string> DirtyPaths {
    get {
      lock (this._lock)
        return [.. this._files.Keys];
    }
  }

  public bool IsDirty(string path) {
    lock (this._lock)
      return this._files.ContainsKey(path);
  }

  public DirtyState StateOf(string path) {
    lock (this._lock)
      return this._files.TryGetValue(path, out var buffer) ? buffer.State : DirtyState.Clean;
  }

  /// <summary>Stages a mutation; false = write-buffer budget exhausted (backpressure, FR-BACKP).</summary>
  public bool StageWrite(string path, long offset, byte[] data, long journalSequence, int durableCopies) {
    lock (this._lock) {
      if (!cache.TryReserveWrite(data.Length))
        return false;

      var buffer = this._Buffer(path, durableCopies);
      buffer.Ops.Add(new(offset, (byte[])data.Clone(), null, this._clock()));
      buffer.ReservedBytes += data.Length;
      if (journalSequence != 0)
        buffer.OpenJournalSequences.Add(journalSequence);

      return true;
    }
  }

  public bool StageTruncate(string path, long length, long journalSequence, int durableCopies) {
    lock (this._lock) {
      var buffer = this._Buffer(path, durableCopies);
      buffer.Ops.Add(new(0, null, length, this._clock()));
      if (journalSequence != 0)
        buffer.OpenJournalSequences.Add(journalSequence);

      return true;
    }
  }

  private FileBuffer _Buffer(string path, int durableCopies) {
    if (!this._files.TryGetValue(path, out var buffer)) {
      this._files.Add(path, buffer = new() { FirstStagedUtc = this._clock() });
      buffer.State = durableCopies == 0 ? DirtyState.RamBuffered : DirtyState.Landed;
      buffer.DurableCopies = durableCopies;
    }

    return buffer;
  }

  /// <summary>Applies the pending image over a block read from disk — the buffer is authoritative (SAFE-COHERE).</summary>
  public byte[] OverlayBlock(string path, long blockIndex, int blockSize, byte[] block) {
    lock (this._lock) {
      if (!this._files.TryGetValue(path, out var buffer) || buffer.Ops.Count == 0)
        return block;

      var blockStart = blockIndex * (long)blockSize;
      // NEVER mutate the caller's array in place — it is the shared page-cache block; a
      // concurrent reader would tear and the cache would end up holding unflushed overlay data
      var result = (byte[])block.Clone();
      foreach (var op in buffer.Ops) {
        if (op.TruncateLength is { } truncateLength) {
          var keep = Math.Clamp(truncateLength - blockStart, 0, result.Length);
          if (keep < result.Length)
            Array.Resize(ref result, (int)keep);
          continue;
        }

        var data = op.Data!;
        var opEnd = op.Offset + data.Length;
        var overlapStart = Math.Max(op.Offset, blockStart);
        var overlapEnd = Math.Min(opEnd, blockStart + blockSize);
        if (overlapStart >= overlapEnd)
          continue;

        var needed = (int)(overlapEnd - blockStart);
        if (result.Length < needed)
          Array.Resize(ref result, needed);

        Array.Copy(data, overlapStart - op.Offset, result, overlapStart - blockStart, overlapEnd - overlapStart);
      }

      return result;
    }
  }

  /// <summary>The logical length including staged appends/truncates.</summary>
  public long OverlayLength(string path, long durableLength) {
    lock (this._lock) {
      if (!this._files.TryGetValue(path, out var buffer))
        return durableLength;

      var length = durableLength;
      foreach (var op in buffer.Ops)
        length = op.TruncateLength ?? Math.Max(length, op.End);

      return length;
    }
  }

  /// <summary>True when the buffer alone holds acked data (performance + acceptVolatileAck).</summary>
  public bool IsVolatileOnly(string path) {
    lock (this._lock)
      return this._files.TryGetValue(path, out var buffer) && buffer.DurableCopies == 0;
  }

  /// <summary>Paths whose defer window elapsed or whose age exceeds the hard bound (FR-DEF).</summary>
  public IReadOnlyList<string> ExpiredPaths(TimeSpan deferWindow, TimeSpan maxDefer) {
    var now = this._clock();
    lock (this._lock)
      return [.. this._files
        .Where(pair => pair.Value.Ops.Count > 0
                       && (now - pair.Value.Ops[^1].StagedUtc >= deferWindow || now - pair.Value.FirstStagedUtc >= maxDefer))
        .Select(pair => pair.Key)];
  }

  /// <summary>
  /// Drops buffered bytes that a later write has already made durable on EVERY copy
  /// (SAFE-NOLOSS): without this, an owed op still sitting in the buffer would flush over the
  /// newer full-coverage write and roll the file back. Ops are trimmed at the superseded
  /// range so bytes outside it (still legitimately owed) survive; truncates are untouched.
  /// </summary>
  public void Supersede(string path, long offset, long length) {
    if (length <= 0)
      return;

    lock (this._lock) {
      if (!this._files.TryGetValue(path, out var buffer))
        return;

      var end = offset + length;
      var kept = new List<PendingOp>();
      foreach (var op in buffer.Ops) {
        if (op.TruncateLength is not null || op.Data is null) {
          kept.Add(op); // a truncate is order-sensitive — never dropped here
          continue;
        }

        var opStart = op.Offset;
        var opEnd = op.Offset + op.Data.Length;
        if (opEnd <= offset || opStart >= end) {
          kept.Add(op); // no overlap with the superseded range
          continue;
        }

        // keep the non-overlapping prefix and suffix; the overlapping middle is now durable
        if (opStart < offset)
          kept.Add(op with { Data = op.Data[..(int)(offset - opStart)] });
        if (opEnd > end)
          kept.Add(new PendingOp(end, op.Data[(int)(end - opStart)..], null, op.StagedUtc));
      }

      var newReserved = kept.Where(o => o.Data != null).Sum(o => (long)o.Data!.Length);
      cache.ReleaseWrite(buffer.ReservedBytes - newReserved);
      buffer.ReservedBytes = newReserved;
      buffer.Ops.Clear();
      buffer.Ops.AddRange(kept);

      if (buffer.Ops.Count == 0)
        this._files.Remove(path);
    }
  }

  /// <summary>Removes and returns the pending image for application; the caller must complete the returned journal sequences.</summary>
  public (IReadOnlyList<PendingOp> ops, IReadOnlyList<long> journalSequences, int durableCopies)? Drain(string path) {
    lock (this._lock) {
      if (!this._files.Remove(path, out var buffer))
        return null;

      cache.ReleaseWrite(buffer.ReservedBytes);
      return ([.. buffer.Ops], [.. buffer.OpenJournalSequences], buffer.DurableCopies);
    }
  }

  /// <summary>Follows a rename so pending mutations land on the new name.</summary>
  public void RenamePath(string from, string to) {
    lock (this._lock) {
      if (!this._files.Remove(from, out var buffer))
        return;

      this._files[to] = buffer;
    }
  }

}
