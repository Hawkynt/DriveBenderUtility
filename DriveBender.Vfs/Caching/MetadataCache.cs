namespace DivisonM.Vfs.Caching;

public enum MetadataKind {
  Stat,
  DirectoryListing,
  Placement,
}

/// <summary>
/// Key of one metadata entry: pool, normalized pool-relative path, kind. The path compares exactly
/// as the rest of the engine does (<see cref="PoolPaths.PathComparison"/>), so on Windows a
/// stat/placement cached under one casing is invalidated by a mutation under another, while on
/// POSIX — where the two names are two files — neither can answer for the other (SAFE-COHERE).
/// </summary>
public sealed record MetadataKey(Guid PoolId, string Path, MetadataKind Kind) {
  public bool Equals(MetadataKey? other)
    => other != null && this.PoolId == other.PoolId && this.Kind == other.Kind
       && string.Equals(this.Path, other.Path, PoolPaths.PathComparison);

  public override int GetHashCode()
    => HashCode.Combine(this.PoolId, PoolPaths.PathComparer.GetHashCode(this.Path), this.Kind);
}

/// <summary>
/// Metadata cache (§6.5): dir listings, stat results and path→placement resolutions,
/// bounded by entry count, expired by TTL, and invalidated on mutation (a write to a
/// path drops the path's entries and its parent's listing).
///
/// LOCKING: a hit takes no lock. Every read of a file looks up its placement AND its stat here,
/// and when both went through one monitor, twenty threads of cached reads spent ~80% of the read
/// path queueing on it (measured: the engine's read rate went from ~500k to ~1M/s across twenty
/// threads, and to several million once hits stopped waiting). The map is concurrent; every
/// CHANGE to it still happens under the lock, together with the matching change to the policy,
/// so the two never disagree about which keys exist. A hit records its recency only when the lock
/// is free at that instant — recency is a hint to eviction, and a single caller always records it,
/// so an uncontended cache evicts exactly as before.
/// </summary>
public sealed class MetadataCache(ICacheEvictionPolicy<MetadataKey> policy, int maxEntries, TimeSpan ttl, Func<DateTime>? clock = null) {

  public MetadataCache(EvictionPolicy policy, int maxEntries, TimeSpan ttl, Func<DateTime>? clock = null)
    : this(EvictionPolicyFactory.Create<MetadataKey>(policy, maxEntries), maxEntries, ttl, clock) { }

  private sealed record Entry(object Value, DateTime ExpiresUtc);

  private readonly ICacheEvictionPolicy<MetadataKey> _policy = policy;
  /// <summary>Read without the lock; written only under it (see the class remarks).</summary>
  private readonly System.Collections.Concurrent.ConcurrentDictionary<MetadataKey, Entry> _entries = new();
  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
  private readonly Lock _lock = new();

  /// <summary>The map's size, kept under the lock: a concurrent map's own Count takes every one of its internal locks.</summary>
  private int _count;

  public int Count => Volatile.Read(ref this._count);

  public bool TryGet<T>(MetadataKey key, out T value) where T : notnull {
    if (!this._entries.TryGetValue(key, out var entry)) {
      value = default!;
      return false;
    }

    if (entry.ExpiresUtc > this._clock() && entry.Value is T typed) {
      // Recency is a hint, so a hit never WAITS to record it. It is recorded only while this exact
      // entry is still the key's — a policy told about a key it no longer tracks may re-admit it
      // (SLRU promotes on access), and the map and the policy must never disagree.
      if (this._lock.TryEnter())
        try {
          if (this._entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            this._policy.OnAccess(key);
        } finally {
          this._lock.Exit();
        }

      value = typed;
      return true;
    }

    // expired, or unable to answer as T: dropped — unless a Put has replaced it meanwhile
    lock (this._lock)
      if (this._entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        this._RemoveLocked(key);

    value = default!;
    return false;
  }

  public void Put(MetadataKey key, object value) {
    lock (this._lock) {
      var entry = new Entry(value, this._clock() + ttl);
      if (this._entries.ContainsKey(key)) {
        this._entries[key] = entry;
        this._policy.OnAccess(key);
      } else {
        this._entries[key] = entry;
        ++this._count;
        this._policy.OnInsert(key);
      }

      while (this._count > maxEntries) {
        var victim = this._policy.SelectVictim();
        if (victim == null)
          break;

        if (this._entries.TryRemove(victim, out _))
          --this._count;
      }
    }
  }

  /// <summary>Invalidation on mutation: drops all kinds for the path plus the parent folder's listing and placement.</summary>
  public void InvalidatePath(Guid poolId, string path) {
    var normalized = PoolPaths.Normalize(path);
    var parent = PoolPaths.GetParent(normalized);
    lock (this._lock) {
      foreach (var kind in Enum.GetValues<MetadataKind>())
        this._RemoveLocked(new(poolId, normalized, kind));

      this._RemoveLocked(new(poolId, parent, MetadataKind.DirectoryListing));
    }
  }

  public void InvalidatePool(Guid poolId) {
    lock (this._lock)
      foreach (var key in this._entries.Keys.Where(k => k.PoolId == poolId).ToArray())
        this._RemoveLocked(key);
  }

  private void _RemoveLocked(MetadataKey key) {
    if (!this._entries.TryRemove(key, out _))
      return;

    --this._count;
    this._policy.Remove(key);
  }

}
