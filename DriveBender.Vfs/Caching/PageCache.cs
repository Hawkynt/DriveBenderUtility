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
/// </summary>
public sealed class PageCache(EvictionPolicy policy, int blockSize) {

  private sealed class PoolShard(EvictionPolicy policy) {
    public readonly ICacheEvictionPolicy<PageKey> Policy = EvictionPolicyFactory.Create<PageKey>(policy);
    public readonly Dictionary<PageKey, byte[]> Blocks = [];

    /// <summary>
    /// path → its cached block indices. InvalidatePath runs on EVERY mutation, and without
    /// this index it scanned every block in the pool to find the handful belonging to one
    /// file — so a write's invalidation cost grew with total cache occupancy rather than with
    /// the file. Kept in exact step with <see cref="Blocks"/>.
    /// </summary>
    public readonly Dictionary<string, HashSet<long>> ByPath = new(StringComparer.OrdinalIgnoreCase);

    public long Bytes;
    public long Hits;
    public long Misses;
    public double Weight = 1.0;
    public long Epoch; // bumped on every invalidation — lets a lock-free prefetch reject a stale late Put

    public void Track(PageKey key) {
      if (!this.ByPath.TryGetValue(key.Path, out var blocks))
        this.ByPath.Add(key.Path, blocks = []);

      blocks.Add(key.BlockIndex);
    }

    public void Untrack(PageKey key) {
      if (this.ByPath.TryGetValue(key.Path, out var blocks) && blocks.Remove(key.BlockIndex) && blocks.Count == 0)
        this.ByPath.Remove(key.Path);
    }
  }

  private readonly Dictionary<Guid, PoolShard> _shards = [];
  private readonly Lock _lock = new();

  /// <summary>
  /// Running sum of every shard's bytes. The eviction loop consulted this on each iteration
  /// and previously re-summed all shards every time — kept incrementally instead so a Put
  /// that evicts many blocks stays linear in the blocks it evicts.
  /// </summary>
  private long _totalBytes;

  public int BlockSize { get; } = blockSize;

  /// <summary>Budget in bytes; changed live by the owning instance's split controller.</summary>
  public long BudgetBytes { get; private set; }

  public long TotalBytes {
    get {
      lock (this._lock)
        return this._totalBytes;
    }
  }

  public void SetBudget(long bytes) {
    lock (this._lock) {
      this.BudgetBytes = Math.Max(0, bytes);
      this._EvictUntilWithinBudget();
    }
  }

  public void SetPoolWeight(Guid poolId, double weight) {
    lock (this._lock)
      this._Shard(poolId).Weight = Math.Max(double.Epsilon, weight);
  }

  private PoolShard _Shard(Guid poolId) {
    if (!this._shards.TryGetValue(poolId, out var shard))
      this._shards.Add(poolId, shard = new(policy));

    return shard;
  }

  public bool TryGet(PageKey key, out byte[] block) {
    lock (this._lock) {
      var shard = this._Shard(key.PoolId);
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
    lock (this._lock)
      this._PutLocked(key, block);
  }

  /// <summary>The current invalidation epoch of a pool — captured before a lock-free background load.</summary>
  public long EpochOf(Guid poolId) {
    lock (this._lock)
      return this._Shard(poolId).Epoch;
  }

  /// <summary>
  /// Inserts a block only if the pool has not been invalidated since <paramref name="expectedEpoch"/>
  /// was captured — so a prefetch that read a block off disk BEFORE a concurrent write invalidated
  /// the path can never poison the cache with pre-write bytes (SAFE-COHERE).
  /// </summary>
  public void PutIfCurrent(PageKey key, byte[] block, long expectedEpoch) {
    lock (this._lock) {
      if (this._Shard(key.PoolId).Epoch != expectedEpoch)
        return;

      this._PutLocked(key, block);
    }
  }

  private void _PutLocked(PageKey key, byte[] block) {
    var shard = this._Shard(key.PoolId);
    if (shard.Blocks.TryGetValue(key, out var existing)) {
      shard.Bytes += block.Length - existing.Length;
      this._totalBytes += block.Length - existing.Length;
      shard.Blocks[key] = block;
      shard.Policy.OnAccess(key);
    } else {
      shard.Blocks.Add(key, block);
      shard.Track(key);
      shard.Bytes += block.Length;
      this._totalBytes += block.Length;
      shard.Policy.OnInsert(key);
    }

    this._EvictUntilWithinBudget();
  }

  /// <summary>Coherency (SAFE-COHERE): drop every cached block of a path after a mutation.</summary>
  public void InvalidatePath(Guid poolId, string path) {
    lock (this._lock) {
      if (!this._shards.TryGetValue(poolId, out var shard))
        return;

      ++shard.Epoch; // any in-flight background load's Put for this pool is now rejected
      if (!shard.ByPath.Remove(path, out var blocks))
        return; // nothing of this path is cached — O(1), not a scan of the whole shard

      foreach (var blockIndex in blocks) {
        var key = new PageKey(poolId, path, blockIndex);
        if (!shard.Blocks.Remove(key, out var block))
          continue;

        shard.Bytes -= block.Length;
        this._totalBytes -= block.Length;
        shard.Policy.Remove(key);
      }
    }
  }

  public void InvalidatePool(Guid poolId) {
    lock (this._lock) {
      if (this._shards.TryGetValue(poolId, out var shard)) {
        ++shard.Epoch; // preserved across the reset below so late Puts capturing the old epoch are rejected
        this._totalBytes -= shard.Bytes;
      }

      this._shards.Remove(poolId);
    }
  }

  public CacheStatistics GetStatistics(Guid poolId) {
    lock (this._lock) {
      return this._shards.TryGetValue(poolId, out var shard)
        ? new(shard.Hits, shard.Misses, shard.Bytes, shard.Blocks.Count)
        : new(0, 0, 0, 0);
    }
  }

  private void _EvictUntilWithinBudget() {
    while (this._totalBytes > this.BudgetBytes) {
      // weighted fair share: evict from the pool most over bytes/weight
      var victimShard = this._shards.Values.Where(s => s.Blocks.Count > 0).MaxBy(s => s.Bytes / s.Weight);
      if (victimShard == null)
        return;

      var victim = victimShard.Policy.SelectVictim();
      if (victim == null)
        return;

      if (!victimShard.Blocks.Remove(victim, out var block))
        continue; // the policy named a key the shard no longer holds — drop it and keep going

      victimShard.Untrack(victim);
      victimShard.Bytes -= block.Length;
      this._totalBytes -= block.Length;
    }
  }

}
