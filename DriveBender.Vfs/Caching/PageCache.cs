namespace DivisonM.Vfs.Caching;

/// <summary>
/// Key of one cached block: pool, normalized pool-relative path, block index. The path
/// compares case-INSENSITIVELY to match the rest of the engine (handles, write buffer,
/// staging all use OrdinalIgnoreCase) — otherwise an invalidation under one casing would
/// leave a stale block cached under another for a full TTL (SAFE-COHERE).
/// </summary>
public sealed record PageKey(Guid PoolId, string Path, long BlockIndex) {
  public bool Equals(PageKey? other)
    => other != null && this.PoolId == other.PoolId && this.BlockIndex == other.BlockIndex
       && string.Equals(this.Path, other.Path, StringComparison.OrdinalIgnoreCase);

  public override int GetHashCode()
    => HashCode.Combine(this.PoolId, StringComparer.OrdinalIgnoreCase.GetHashCode(this.Path), this.BlockIndex);
}

public sealed record CacheStatistics(long Hits, long Misses, long Bytes, int Entries) {
  public double HitRate => this.Hits + this.Misses == 0 ? 0 : (double)this.Hits / (this.Hits + this.Misses);
}

/// <summary>
/// Block-aligned read (page) cache (§6.5). Pools sharing an instance compete under
/// weighted fair-share eviction: the victim always comes from the pool most over its
/// weighted share, so a busy pool can never fully starve another (FR-CACHE-GLOBAL);
/// hits and occupancy are tracked per pool.
///
/// LOCKING: each pool holds its OWN lock, and no code path ever holds two at once, so pools
/// never contend with each other and no lock ordering exists to get wrong. A single global lock
/// meant every cached read in the process — including the parallel mirror-split and prefetch
/// loads that exist precisely to use several disks at once — queued behind one another.
/// </summary>
public sealed class PageCache(EvictionPolicy policy, int blockSize) {

  private sealed class PoolShard(EvictionPolicy policy) {

    /// <summary>Guards everything below. Taken alone, never while holding another shard's lock.</summary>
    public readonly Lock Lock = new();

    public readonly ICacheEvictionPolicy<PageKey> Policy = EvictionPolicyFactory.Create<PageKey>(policy);
    public readonly Dictionary<PageKey, byte[]> Blocks = [];

    /// <summary>
    /// path → its cached block indices. InvalidatePath runs on EVERY mutation, and without
    /// this index it scanned every block in the pool to find the handful belonging to one
    /// file — so a write's invalidation cost grew with total cache occupancy rather than with
    /// the file. Kept in exact step with <see cref="Blocks"/>.
    /// </summary>
    public readonly Dictionary<string, HashSet<long>> ByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Occupancy. Read WITHOUT the shard lock during victim selection, so it moves atomically.</summary>
    public long Bytes;

    public long Hits;
    public long Misses;
    public double Weight = 1.0;

    /// <summary>Bumped on every invalidation — lets a lock-free prefetch reject a stale late Put.</summary>
    public long Epoch;

    public void Track(PageKey key) {
      if (!this.ByPath.TryGetValue(key.Path, out var blocks))
        this.ByPath.Add(key.Path, blocks = []);

      blocks.Add(key.BlockIndex);
    }

    public void Untrack(PageKey key) {
      if (this.ByPath.TryGetValue(key.Path, out var blocks) && blocks.Remove(key.BlockIndex) && blocks.Count == 0)
        this.ByPath.Remove(key.Path);
    }

    /// <summary>Drops every block; the caller holds <see cref="Lock"/>. The epoch deliberately SURVIVES.</summary>
    public void Clear() {
      this.Blocks.Clear();
      this.ByPath.Clear();
      this.Policy.Clear();
      Interlocked.Exchange(ref this.Bytes, 0);
      this.Hits = 0;
      this.Misses = 0;
    }
  }

  private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PoolShard> _shards = new();

  /// <summary>Running sum of every shard's bytes, maintained atomically so the budget can be checked without any lock.</summary>
  private long _totalBytes;

  private long _budgetBytes;

  public int BlockSize { get; } = blockSize;

  /// <summary>Budget in bytes; changed live by the owning instance's split controller.</summary>
  public long BudgetBytes => Interlocked.Read(ref this._budgetBytes);

  public long TotalBytes => Interlocked.Read(ref this._totalBytes);

  public void SetBudget(long bytes) {
    Interlocked.Exchange(ref this._budgetBytes, Math.Max(0, bytes));
    this._EvictUntilWithinBudget();
  }

  public void SetPoolWeight(Guid poolId, double weight) {
    var shard = this._Shard(poolId);
    lock (shard.Lock)
      shard.Weight = Math.Max(double.Epsilon, weight);
  }

  private PoolShard _Shard(Guid poolId) => this._shards.GetOrAdd(poolId, static (_, p) => new(p), policy);

  public bool TryGet(PageKey key, out byte[] block) {
    var shard = this._Shard(key.PoolId);
    lock (shard.Lock) {
      if (shard.Blocks.TryGetValue(key, out var found)) {
        ++shard.Hits;
        shard.Policy.OnAccess(key);
        block = found;
        return true;
      }

      ++shard.Misses;
      block = [];
      return false;
    }
  }

  public void Put(PageKey key, byte[] block) {
    var shard = this._Shard(key.PoolId);
    lock (shard.Lock)
      _PutLocked(shard, key, block, ref this._totalBytes);

    // deliberately OUTSIDE the shard lock: eviction may have to take a DIFFERENT pool's lock
    // (weighted fair share picks the pool most over its share), and holding one lock while
    // reaching for another is the one way this design could deadlock
    this._EvictUntilWithinBudget();
  }

  /// <summary>The current invalidation epoch of a pool — captured before a lock-free background load.</summary>
  public long EpochOf(Guid poolId) {
    var shard = this._Shard(poolId);
    lock (shard.Lock)
      return shard.Epoch;
  }

  /// <summary>
  /// Inserts a block only if the pool has not been invalidated since <paramref name="expectedEpoch"/>
  /// was captured — so a prefetch that read a block off disk BEFORE a concurrent write invalidated
  /// the path can never poison the cache with pre-write bytes (SAFE-COHERE).
  /// </summary>
  public void PutIfCurrent(PageKey key, byte[] block, long expectedEpoch) {
    var shard = this._Shard(key.PoolId);
    lock (shard.Lock) {
      if (shard.Epoch != expectedEpoch)
        return;

      _PutLocked(shard, key, block, ref this._totalBytes);
    }

    this._EvictUntilWithinBudget();
  }

  private static void _PutLocked(PoolShard shard, PageKey key, byte[] block, ref long totalBytes) {
    if (shard.Blocks.TryGetValue(key, out var existing)) {
      var delta = block.Length - existing.Length;
      Interlocked.Add(ref shard.Bytes, delta);
      Interlocked.Add(ref totalBytes, delta);
      shard.Blocks[key] = block;
      shard.Policy.OnAccess(key);
      return;
    }

    shard.Blocks.Add(key, block);
    shard.Track(key);
    Interlocked.Add(ref shard.Bytes, block.Length);
    Interlocked.Add(ref totalBytes, block.Length);
    shard.Policy.OnInsert(key);
  }

  /// <summary>Coherency (SAFE-COHERE): drop every cached block of a path after a mutation.</summary>
  public void InvalidatePath(Guid poolId, string path) {
    if (!this._shards.TryGetValue(poolId, out var shard))
      return;

    lock (shard.Lock) {
      ++shard.Epoch; // any in-flight background load's Put for this pool is now rejected
      if (!shard.ByPath.Remove(path, out var blocks))
        return; // nothing of this path is cached — O(1), not a scan of the whole shard

      foreach (var blockIndex in blocks) {
        var key = new PageKey(poolId, path, blockIndex);
        if (!shard.Blocks.Remove(key, out var block))
          continue;

        Interlocked.Add(ref shard.Bytes, -block.Length);
        Interlocked.Add(ref this._totalBytes, -block.Length);
        shard.Policy.Remove(key);
      }
    }
  }

  /// <summary>
  /// Drops the whole pool's cache. The shard is CLEARED rather than removed so its epoch
  /// survives: replacing it with a fresh shard restarted the epoch at zero, and a prefetch that
  /// had captured epoch zero before the invalidation would then be accepted afterwards — putting
  /// back exactly the pre-invalidation block the epoch exists to reject (SAFE-COHERE).
  /// </summary>
  public void InvalidatePool(Guid poolId) {
    if (!this._shards.TryGetValue(poolId, out var shard))
      return;

    lock (shard.Lock) {
      ++shard.Epoch;
      Interlocked.Add(ref this._totalBytes, -Interlocked.Read(ref shard.Bytes));
      shard.Clear();
    }
  }

  public CacheStatistics GetStatistics(Guid poolId) {
    if (!this._shards.TryGetValue(poolId, out var shard))
      return new(0, 0, 0, 0);

    lock (shard.Lock)
      return new(shard.Hits, shard.Misses, Interlocked.Read(ref shard.Bytes), shard.Blocks.Count);
  }

  private void _EvictUntilWithinBudget() {
    // A bounded number of attempts: concurrent writers may keep the total above budget for a
    // moment, and spinning here would burn the very CPU the cache exists to save. The budget is
    // a target, not a hard wall — the next Put resumes the work.
    for (var attempt = 0; attempt < 4096 && Interlocked.Read(ref this._totalBytes) > this.BudgetBytes; ++attempt) {
      var victim = this._SelectVictimShard();
      if (victim == null || !this._EvictOne(victim))
        return;
    }
  }

  /// <summary>
  /// The pool most over its weighted share. Scanned WITHOUT holding any lock — occupancy moves
  /// atomically, and picking a slightly stale victim only costs fairness, never correctness.
  /// A LINQ MaxBy here allocated an enumerator and a comparer per EVICTED BLOCK.
  /// </summary>
  private PoolShard? _SelectVictimShard() {
    PoolShard? victim = null;
    var worst = double.NegativeInfinity;
    foreach (var shard in this._shards.Values) {
      var bytes = Interlocked.Read(ref shard.Bytes);
      if (bytes <= 0)
        continue;

      var pressure = bytes / shard.Weight;
      if (pressure <= worst)
        continue;

      worst = pressure;
      victim = shard;
    }

    return victim;
  }

  /// <summary>Evicts one block from a shard; false when it has nothing left to give.</summary>
  private bool _EvictOne(PoolShard shard) {
    lock (shard.Lock) {
      while (true) {
        var victim = shard.Policy.SelectVictim();
        if (victim == null)
          return false;

        if (!shard.Blocks.Remove(victim, out var block))
          continue; // the policy named a key the shard no longer holds — drop it and keep going

        shard.Untrack(victim);
        Interlocked.Add(ref shard.Bytes, -block.Length);
        Interlocked.Add(ref this._totalBytes, -block.Length);
        return true;
      }
    }
  }

}
