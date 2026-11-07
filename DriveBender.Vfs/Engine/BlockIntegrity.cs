using System.Collections.Concurrent;
using DivisonM.Vfs.Integrity;

namespace DivisonM.Vfs.Engine;

/// <summary>
/// Per-shard checking and parity repair for a pool's files (docs/Issues.md, "Integrity v2").
///
/// Every copy of a file of at least <see cref="MinFileBytes"/> gets a <see cref="BlockIndex"/> kept
/// beside it on the same member, under the utility folder — the data file itself is never touched,
/// so a pool stays readable without this. A read checks the shards it serves against the copy's
/// index; a damaged shard is served from another copy, or rebuilt from the copy's own parity when
/// every copy is damaged there, and the damaged copy is then repaired.
///
/// An index is only ever believed while it describes the copy: same length and modification time,
/// and the path not changed by the engine since it was built. The engine reports every change
/// (<see cref="Changed"/>) under the path's write lease, before any reader can see the new bytes;
/// after a crash, recovery drops the indexes of every file a write intent names. An index that is
/// stale but looks current would make a repair write OLD content over an acknowledged write, so
/// this is the invariant everything else rests on.
/// </summary>
public sealed class BlockIntegrity(IReadOnlyList<IVolumeIO> members, int parityShards, long minFileBytes, Action<IVolumeIO, long>? admit = null) {

  public const string IndexFolder = PoolPaths.UtilityFolderName + "/blocks";
  private const string _SUFFIX = ".dbi";
  private const int _MAX_CACHED = 4096;

  public int ParityShards { get; } = parityShards;
  public long MinFileBytes { get; } = minFileBytes;

  private readonly record struct CacheKey(Guid Member, bool Shadow, string Path);
  private readonly record struct Cached(long Length, long MTimeTicks, BlockIndex? Index);

  private readonly ConcurrentDictionary<CacheKey, Cached> _cache = new();

  /// <summary>Paths the engine changed whose indexes are not to be believed until rebuilt.</summary>
  private readonly ConcurrentDictionary<string, byte> _stale = new(PoolPaths.PathComparer);

  /// <summary>Files waiting for an index.</summary>
  private readonly ConcurrentQueue<string> _toIndex = new();
  private readonly ConcurrentDictionary<string, byte> _queued = new(PoolPaths.PathComparer);

  private long _shardsChecked;
  private long _shardsRepaired;

  public long ShardsChecked => Interlocked.Read(ref this._shardsChecked);
  public long ShardsRepaired => Interlocked.Read(ref this._shardsRepaired);

  public static string IndexPathFor(string normalized, bool shadow) => $"{IndexFolder}/{PoolPaths.ToPhysical(normalized, shadow)}{_SUFFIX}";

  // ---- staleness --------------------------------------------------------------------------------

  /// <summary>
  /// The engine changed this path's content (or is about to): its indexes stop being believed at
  /// once, are deleted in the background, and the file is queued to be indexed again once it settles.
  /// </summary>
  public void Changed(string normalized) {
    // a staging temp never has an index, and marking every temp would grow the set without end
    if (normalized.EndsWith("." + DriveBender.DriveBenderConstants.TEMP_EXTENSION, StringComparison.OrdinalIgnoreCase))
      return;

    if (!this._stale.TryAdd(normalized, 0))
      return; // already stale: a file under continuous writes pays this once

    // The mark set grows with every file written; past a bound the marks whose indexes are known to
    // be deleted are let go — the mark only spared later writes the deletion, so such a path written
    // again just pays it once more. A path whose deletion FAILED keeps its mark: its old index may
    // still be on a member, and the mark is all that keeps it from being believed.
    if (this._stale.Count > 200_000)
      foreach (var path in this._stale.Keys)
        if (!this._undeleted.ContainsKey(path) && !PoolPaths.PathComparer.Equals(path, normalized))
          this._stale.TryRemove(path, out _);

    foreach (var key in this._cache.Keys)
      if (PoolPaths.PathComparer.Equals(key.Path, normalized))
        this._cache.TryRemove(key, out _);

    // Deleted now, not in the background: a deleted file must leave nothing under its name, and an
    // index that outlives the content it describes is exactly what this layer must never have.
    if (DeleteIndexes(members, normalized) && members.All(m => m.IsOnline))
      this._undeleted.TryRemove(normalized, out _);
    else
      this._undeleted[normalized] = 0;
  }

  /// <summary>Paths whose index deletion did not reach every member; their stale mark is never let go.</summary>
  private readonly ConcurrentDictionary<string, byte> _undeleted = new(PoolPaths.PathComparer);

  /// <summary>Recovery: a write intent named this path, so whatever index it has may be stale.</summary>

  /// <summary>Deletes every index of a path on the members given; false when one could not be deleted.</summary>
  public static bool DeleteIndexes(IEnumerable<IVolumeIO> members, string normalized) {
    var all = true;
    foreach (var member in members.Where(m => m.IsOnline))
      foreach (var shadow in new[] { false, true })
        try {
          var path = IndexPathFor(normalized, shadow);
          if (member.FileExists(path, false))
            member.Delete(path, false);
        } catch (PoolFsException) {
          all = false; // its length or time gives it away, and the stale mark keeps it out of use
        }

    return all;
  }

  // ---- lookup -----------------------------------------------------------------------------------

  /// <summary>The copy's index, when one exists and describes the copy as it is now.</summary>
  public BlockIndex? IndexFor(PhysicalCopy copy, string normalized, FileMeta meta) {
    if (meta.Length < this.MinFileBytes || this._stale.ContainsKey(normalized))
      return null;

    var key = new CacheKey(copy.Volume.MemberId, copy.Shadow, normalized);
    if (this._cache.TryGetValue(key, out var cached) && cached.Length == meta.Length && cached.MTimeTicks == meta.LastWriteTimeUtc.Ticks)
      return cached.Index;

    BlockIndex? index = null;
    try {
      var path = IndexPathFor(normalized, copy.Shadow);
      if (copy.Volume.FileExists(path, false)) {
        using var stream = copy.Volume.OpenRead(path, false);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        index = BlockIndex.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
      }
    } catch (PoolFsException) {
      index = null;
    }

    if (index != null && !index.Describes(meta.Length, meta.LastWriteTimeUtc))
      index = null; // an older version's index

    // a change reported while the file was being read must win: caching what was read would bring
    // back the index the change just deleted
    if (this._stale.ContainsKey(normalized))
      return null;

    if (this._cache.Count >= _MAX_CACHED)
      this._cache.Clear();
    this._cache[key] = new(meta.Length, meta.LastWriteTimeUtc.Ticks, index);
    return index;
  }

  // ---- verification on read ---------------------------------------------------------------------

  /// <summary>
  /// Checks the shards of <paramref name="block"/> (the copy's bytes at <paramref name="offset"/>)
  /// against the copy's index. Returns the damaged shards; empty when everything checked out or
  /// the copy has no usable index. A shard reaching past the block is read whole from the copy.
  /// </summary>
  public IReadOnlyList<(int Stripe, int Shard)> Check(PhysicalCopy copy, string normalized, long offset, ReadOnlySpan<byte> block) {
    if (block.Length == 0)
      return [];

    FileMeta meta;
    try {
      if (copy.Volume.Stat(normalized, copy.Shadow) is not { IsDirectory: false } found)
        return [];
      meta = found;
    } catch (PoolFsException) {
      return [];
    }

    var index = this.IndexFor(copy, normalized, meta);
    if (index == null) {
      if (meta.Length >= this.MinFileBytes && !this._stale.ContainsKey(normalized))
        this.Enqueue(normalized); // in use and not indexed yet: it will be, in the background
      return [];
    }

    List<(int, int)>? damaged = null;
    foreach (var (stripe, shard) in index.ShardsOverlapping(offset, block.Length)) {
      var (start, length) = index.ShardRange(stripe, shard);
      ulong hash;
      if (start >= offset && start + length <= offset + block.Length)
        hash = BlockIndex.Hash(block.Slice((int)(start - offset), length));
      else if (this._ReadRange(copy, normalized, start, length) is { } whole)
        hash = BlockIndex.Hash(whole);
      else {
        (damaged ??= []).Add((stripe, shard));
        continue;
      }

      Interlocked.Increment(ref this._shardsChecked);
      if (hash != index.ExpectedHash(stripe, shard))
        (damaged ??= []).Add((stripe, shard));
    }

    return damaged ?? (IReadOnlyList<(int, int)>)[];
  }

  /// <summary>
  /// The correct bytes for [<paramref name="offset"/>, +<paramref name="count"/>), rebuilt from any
  /// copy's own parity; null when no copy can rebuild them. Each copy is tried on its own: its
  /// index, its shards, its parity — a copy's parity describes that copy and nothing else.
  /// </summary>
  public byte[]? Rebuild(IReadOnlyList<PhysicalCopy> copies, string normalized, long offset, int count) {
    foreach (var copy in copies) {
      FileMeta meta;
      try {
        if (copy.Volume.Stat(normalized, copy.Shadow) is not { IsDirectory: false } found)
          continue;
        meta = found;
      } catch (PoolFsException) {
        continue;
      }

      if (this.IndexFor(copy, normalized, meta) is not { ParityShards: > 0 } index)
        continue;

      var result = new byte[Math.Min(count, Math.Max(0, index.Length - offset))];
      var complete = true;
      foreach (var stripe in index.ShardsOverlapping(offset, result.Length).Select(s => s.Stripe).Distinct()) {
        var shards = index.RepairStripe(stripe, shard => {
          var (start, length) = index.ShardRange(stripe, shard);
          return this._ReadRange(copy, normalized, start, length);
        });
        if (shards == null) {
          complete = false;
          break;
        }

        for (var shard = 0; shard < shards.Length; ++shard) {
          var (start, length) = index.ShardRange(stripe, shard);
          var from = Math.Max(start, offset);
          var to = Math.Min(start + length, offset + result.Length);
          if (from < to)
            shards[shard].AsSpan((int)(from - start), (int)(to - from)).CopyTo(result.AsSpan((int)(from - offset)));
        }
      }

      if (complete)
        return result;
    }

    return null;
  }

  /// <summary>
  /// Writes the correct bytes over a copy's damaged shards, in place, keeping the copy's
  /// modification time so its index goes on describing it. The caller holds the path's write
  /// lease and has re-checked that nothing changed; <paramref name="correct"/> must cover every
  /// damaged shard. Returns how many shards were rewritten.
  /// </summary>
  public int RepairShards(PhysicalCopy copy, string normalized, IReadOnlyList<(int Stripe, int Shard)> damaged, Func<long, int, byte[]?> correct) {
    if (this._stale.ContainsKey(normalized) || copy.Volume.Stat(normalized, copy.Shadow) is not { IsDirectory: false } meta
        || this.IndexFor(copy, normalized, meta) is not { } index)
      return 0;

    var repaired = 0;
    using (var stream = copy.Volume.OpenWrite(normalized, copy.Shadow, false)) {
      foreach (var (stripe, shard) in damaged) {
        var (start, length) = index.ShardRange(stripe, shard);
        if (correct(start, length) is not { } bytes || bytes.Length != length || BlockIndex.Hash(bytes) != index.ExpectedHash(stripe, shard))
          continue; // never write anything the index does not vouch for

        stream.Seek(start, SeekOrigin.Begin);
        stream.Write(bytes);
        ++repaired;
      }

      stream.Flush();
    }

    if (repaired > 0) {
      copy.Volume.SetTimestamps(normalized, copy.Shadow, null, meta.LastWriteTimeUtc); // the index still describes it
      Interlocked.Add(ref this._shardsRepaired, repaired);
    }

    return repaired;
  }

  // ---- building ---------------------------------------------------------------------------------

  /// <summary>Queues a file to be indexed once it is settled.</summary>
  public void Enqueue(string normalized) {
    if (!this._queued.TryAdd(normalized, 0))
      return;

    this._toIndex.Enqueue(normalized);
    this.QueuedCallback?.Invoke();
  }

  /// <summary>True once the pool is shutting down: a build in progress stops at its next chunk.</summary>
  public Func<bool>? Stopping { get; set; }

  /// <summary>Called whenever a file joins the queue — the indexer's wake-up.</summary>
  public Action? QueuedCallback { get; set; }

  public bool TryDequeue(out string normalized) {
    while (this._toIndex.TryDequeue(out normalized!)) {
      this._queued.TryRemove(normalized, out _);
      return true;
    }

    return false;
  }

  public int Queued => this._queued.Count;

  /// <summary>
  /// Indexes every copy of a file, reading each one whole. Copies that do not agree shard for shard
  /// get no index at all: one of them is damaged or behind, and an index built now would enshrine
  /// whichever was read as the truth — the scrub settles that first. Returns the indexes to commit,
  /// one per copy, or null when there is nothing to commit.
  /// </summary>
  /// <param name="baselineOf">
  /// The copy's recorded whole-file checksum, when the checksum database holds one that still
  /// describes it. A copy whose content contradicts it is not indexed: it changed without its size
  /// or time changing — rot — and rot that happened identically on every copy would otherwise be
  /// enshrined as the content to defend.
  /// </param>
  /// <param name="yieldTo">True when the build should give way now (another job started copying the file); the build then stops at its next chunk.</param>
  public List<(PhysicalCopy Copy, BlockIndex Index)>? Build(string normalized, IReadOnlyList<PhysicalCopy> copies, Func<PhysicalCopy, FileMeta, string?>? baselineOf = null,
    Func<bool>? yieldTo = null) {
    var built = new List<(PhysicalCopy, BlockIndex)>();
    foreach (var copy in copies) {
      try {
        if (copy.Volume.Stat(normalized, copy.Shadow) is not { IsDirectory: false } meta || meta.Length < this.MinFileBytes)
          return null;

        var stopping = this.Stopping;
        using var stream = new _HashingStream(copy.Volume.OpenRead(normalized, copy.Shadow), copy.Volume, admit,
          () => stopping?.Invoke() == true || yieldTo?.Invoke() == true);
        var index = BlockIndex.Build(stream, meta.Length, meta.LastWriteTimeUtc.Ticks, this.ParityShards);
        if (baselineOf?.Invoke(copy, meta) is { } baseline && !string.Equals(baseline, stream.Hex, StringComparison.OrdinalIgnoreCase)) {
          DriveBender.Logger($"[Warning]'{normalized}' on '{copy.Volume.DisplayName}' no longer matches its recorded checksum though its size and time are unchanged — not indexed; the scrub decides which copy is right");
          return null;
        }

        built.Add((copy, index));
      } catch (Exception e) when (e is PoolFsException or IOException) {
        return null;
      }
    }

    if (built.Count == 0 || built.Any(b => !b.Item2.SameContentAs(built[0].Item2)))
      return null;

    return built;
  }

  /// <summary>Reads through, hashing the whole file the way the checksum database does.</summary>
  /// <summary>
  /// Paced like any other bulk read — chunk by chunk, so a member held to a crawl is admitted a piece
  /// at a time rather than asked once for the whole file — and abandoned at the next chunk when the
  /// pool shuts down: an index not finished is simply built again later.
  /// </summary>
  private sealed class _HashingStream(Stream inner, IVolumeIO volume, Action<IVolumeIO, long>? admit, Func<bool>? stopping) : Stream {
    private const int _CHUNK = 1 << 20;
    private readonly System.IO.Hashing.XxHash3 _hash = new();
    public string Hex => Convert.ToHexString(this._hash.GetCurrentHash());
    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) {
      if (stopping?.Invoke() == true)
        throw new OperationCanceledException("the pool is shutting down");

      var slice = buffer[..Math.Min(buffer.Length, _CHUNK)];
      admit?.Invoke(volume, slice.Length);
      var read = inner.Read(slice);
      this._hash.Append(slice[..read]);
      return read;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) {
      if (disposing)
        inner.Dispose();
      base.Dispose(disposing);
    }
  }

  /// <summary>
  /// Saves indexes built by <see cref="Build"/>. The caller holds the path's write lease and has
  /// confirmed the file did not change since the build began. An index whose copy moved on since
  /// is not saved.
  /// </summary>
  public void Commit(string normalized, IReadOnlyList<(PhysicalCopy Copy, BlockIndex Index)> built) {
    foreach (var (copy, index) in built) {
      if (copy.Volume.Stat(normalized, copy.Shadow) is not { } meta || !index.Describes(meta.Length, meta.LastWriteTimeUtc))
        continue;

      var path = IndexPathFor(normalized, copy.Shadow);
      var temp = path + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
      try {
        copy.Volume.EnsureFolder(PoolPaths.GetParent(path), false);
        using (var stream = copy.Volume.OpenWrite(temp, false, true)) {
          stream.SetLength(0);
          stream.Write(index.Serialize());
          stream.Flush();
        }

        copy.Volume.AtomicReplace(temp, path, false);
        this._cache[new(copy.Volume.MemberId, copy.Shadow, normalized)] = new(meta.Length, meta.LastWriteTimeUtc.Ticks, index);
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Could not save the block index of '{normalized}' on '{copy.Volume.DisplayName}': {e.Message}");
      }
    }

    this._stale.TryRemove(normalized, out _);
  }

  private byte[]? _ReadRange(PhysicalCopy copy, string normalized, long start, int length) {
    try {
      using var stream = copy.Volume.OpenRead(normalized, copy.Shadow);
      stream.Seek(start, SeekOrigin.Begin);
      var bytes = new byte[length];
      var read = 0;
      while (read < length && stream.Read(bytes, read, length - read) is var got and > 0)
        read += got;
      return read == length ? bytes : null;
    } catch (Exception e) when (e is PoolFsException or IOException) {
      return null;
    }
  }

}
