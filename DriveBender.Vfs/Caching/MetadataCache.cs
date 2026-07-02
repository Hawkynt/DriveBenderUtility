namespace DivisonM.Vfs.Caching;

public enum MetadataKind {
  Stat,
  DirectoryListing,
  Placement,
}

/// <summary>Key of one metadata entry: pool, normalized pool-relative path, kind.</summary>
public sealed record MetadataKey(Guid PoolId, string Path, MetadataKind Kind);

/// <summary>
/// Metadata cache (§6.5): dir listings, stat results and path→placement resolutions,
/// bounded by entry count, expired by TTL, and invalidated on mutation (a write to a
/// path drops the path's entries and its parent's listing).
/// </summary>
public sealed class MetadataCache(EvictionPolicy policy, int maxEntries, TimeSpan ttl, Func<DateTime>? clock = null) {

  private sealed record Entry(object Value, DateTime ExpiresUtc);

  private readonly ICacheEvictionPolicy<MetadataKey> _policy = EvictionPolicyFactory.Create<MetadataKey>(policy, maxEntries);
  private readonly Dictionary<MetadataKey, Entry> _entries = [];
  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
  private readonly Lock _lock = new();

  public long Hits { get; private set; }
  public long Misses { get; private set; }
  public int Count {
    get {
      lock (this._lock)
        return this._entries.Count;
    }
  }

  public bool TryGet<T>(MetadataKey key, out T value) where T : notnull {
    lock (this._lock) {
      if (this._entries.TryGetValue(key, out var entry) && entry.ExpiresUtc > this._clock() && entry.Value is T typed) {
        ++this.Hits;
        this._policy.OnAccess(key);
        value = typed;
        return true;
      }

      if (this._entries.Remove(key))
        this._policy.Remove(key); // expired

      ++this.Misses;
      value = default!;
      return false;
    }
  }

  public void Put(MetadataKey key, object value) {
    lock (this._lock) {
      var entry = new Entry(value, this._clock() + ttl);
      if (this._entries.ContainsKey(key)) {
        this._entries[key] = entry;
        this._policy.OnAccess(key);
      } else {
        this._entries.Add(key, entry);
        this._policy.OnInsert(key);
      }

      while (this._entries.Count > maxEntries) {
        var victim = this._policy.SelectVictim();
        if (victim == null)
          break;

        this._entries.Remove(victim);
      }
    }
  }

  /// <summary>Invalidation on mutation: drops all kinds for the path plus the parent folder's listing and placement.</summary>
  public void InvalidatePath(Guid poolId, string path) {
    var normalized = PoolPaths.Normalize(path);
    var parent = PoolPaths.GetParent(normalized);
    lock (this._lock) {
      foreach (var kind in Enum.GetValues<MetadataKind>())
        this._Remove(new(poolId, normalized, kind));

      this._Remove(new(poolId, parent, MetadataKind.DirectoryListing));
    }
  }

  public void InvalidatePool(Guid poolId) {
    lock (this._lock)
      foreach (var key in this._entries.Keys.Where(k => k.PoolId == poolId).ToArray())
        this._Remove(key);
  }

  private void _Remove(MetadataKey key) {
    if (this._entries.Remove(key))
      this._policy.Remove(key);
  }

}
