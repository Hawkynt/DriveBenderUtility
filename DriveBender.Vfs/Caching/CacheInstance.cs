namespace DivisonM.Vfs.Caching;

/// <summary>
/// One named, allocatable cache instance (§6.5A): a RAM budget split across the read
/// (page) cache and the write buffer per its split mode, plus an independently sized
/// metadata cache. Pools attach to an instance; dedicated instances are private.
/// </summary>
public sealed class CacheInstance {

  private readonly Lock _lock = new();
  private readonly CacheSplitMode _splitMode;
  private readonly double _fixedReadFraction;
  private long _writeReserved;

  // shared-auto safety bounds: neither side may starve to zero (§6.5A)
  private const double _MIN_FRACTION = 0.1;
  private const double _MAX_FRACTION = 0.9;
  private double _autoReadFraction = 0.6;

  public string Name { get; }
  public long SizeBytes { get; }
  public long ReadCacheMax { get; private set; }
  public long WriteBufferMax { get; private set; }
  public PageCache Pages { get; }
  public MetadataCache Metadata { get; }
  public bool Dynamic { get; }

  public CacheInstance(string name, CacheInstanceConfig config, Func<DateTime>? clock = null) {
    this.Name = name;
    var blockSize = (int)SizeSpec.ParseBytes(config.BlockSize ?? "1MiB");
    var split = config.Split ?? new CacheSplitConfig();
    this._splitMode = split.Mode ?? CacheSplitMode.SharedAuto;
    this.Dynamic = config.Dynamic ?? false;

    switch (this._splitMode) {
      case CacheSplitMode.Separate:
        this.ReadCacheMax = SizeSpec.ParseBytes(split.ReadCacheMax ?? "512MiB");
        this.WriteBufferMax = SizeSpec.ParseBytes(split.WriteBufferMax ?? "512MiB");
        this.SizeBytes = this.ReadCacheMax + this.WriteBufferMax;
        break;

      case CacheSplitMode.SharedFixed: {
        this.SizeBytes = SizeSpec.ParseBytes(config.Size ?? "4GiB");
        this._fixedReadFraction = (SizeSpec.Parse(split.Read ?? "70%").Percent ?? 70) / 100.0;
        this.ReadCacheMax = (long)(this.SizeBytes * this._fixedReadFraction);
        this.WriteBufferMax = this.SizeBytes - this.ReadCacheMax;
        break;
      }

      default: // shared-auto
        this.SizeBytes = SizeSpec.ParseBytes(config.Size ?? "4GiB");
        this.ReadCacheMax = (long)(this.SizeBytes * this._autoReadFraction);
        this.WriteBufferMax = this.SizeBytes - this.ReadCacheMax;
        break;
    }

    this.Pages = new(config.ReadEviction ?? EvictionPolicy.Arc, blockSize);
    this.Pages.SetBudget(this.ReadCacheMax);
    this.Metadata = new(
      config.MetadataEviction ?? EvictionPolicy.Lru,
      config.MetadataEntries ?? 100_000,
      config.MetadataTtl == null ? TimeSpan.FromSeconds(30) : DurationSpec.Parse(config.MetadataTtl),
      clock);
  }

  public long WriteBytesReserved {
    get {
      lock (this._lock)
        return this._writeReserved;
    }
  }

  /// <summary>
  /// Reserves write-buffer RAM for dirty data. Returns false when the hard cap is
  /// reached — the caller must block or degrade to write-through, never grow unbounded
  /// (FR-BACKP). Under shared-auto a write burst steals budget from the read side within
  /// the safety bounds.
  /// </summary>
  public bool TryReserveWrite(long bytes) {
    lock (this._lock) {
      if (this._writeReserved + bytes <= this.WriteBufferMax) {
        this._writeReserved += bytes;
        return true;
      }

      if (this._splitMode == CacheSplitMode.SharedAuto && this._TryShiftTowardWrites(bytes)) {
        this._writeReserved += bytes;
        return true;
      }

      return false;
    }
  }

  public void ReleaseWrite(long bytes) {
    lock (this._lock) {
      this._writeReserved = Math.Max(0, this._writeReserved - bytes);
      if (this._splitMode == CacheSplitMode.SharedAuto)
        this._RebalanceTowardReads();
    }
  }

  private bool _TryShiftTowardWrites(long needed) {
    var demandedWriteBytes = this._writeReserved + needed;
    var wantedReadFraction = 1.0 - (double)demandedWriteBytes / this.SizeBytes;
    var newReadFraction = Math.Max(_MIN_FRACTION, wantedReadFraction);
    if ((long)(this.SizeBytes * (1.0 - newReadFraction)) < demandedWriteBytes)
      return false;

    this._autoReadFraction = newReadFraction;
    this._ApplyAutoSplit();
    return true;
  }

  private void _RebalanceTowardReads() {
    var usedWriteFraction = (double)this._writeReserved / this.SizeBytes;
    var idealReadFraction = Math.Clamp(1.0 - usedWriteFraction - 0.1, _MIN_FRACTION, _MAX_FRACTION);
    if (idealReadFraction <= this._autoReadFraction)
      return;

    this._autoReadFraction = idealReadFraction;
    this._ApplyAutoSplit();
  }

  private void _ApplyAutoSplit() {
    this.ReadCacheMax = (long)(this.SizeBytes * this._autoReadFraction);
    this.WriteBufferMax = this.SizeBytes - this.ReadCacheMax;
    this.Pages.SetBudget(this.ReadCacheMax);
  }

  public void InvalidatePath(Guid poolId, string path) {
    this.Pages.InvalidatePath(poolId, PoolPaths.Normalize(path));
    this.Metadata.InvalidatePath(poolId, path);
  }

}

/// <summary>
/// Machine-wide cache ownership (SAFE-RAM-BUDGET): all instances — the shared global one
/// plus every dedicated one — are created here and their sum is validated against the
/// host ceiling, which may never be over-committed.
/// </summary>
public sealed class CacheHost(long maxTotalBytes) {

  private readonly Dictionary<string, CacheInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<Guid, CacheInstance> _attachments = [];
  private readonly Lock _lock = new();

  public long MaxTotalBytes { get; } = maxTotalBytes;

  public long TotalCommittedBytes {
    get {
      lock (this._lock)
        return this._instances.Values.Sum(i => i.SizeBytes);
    }
  }

  public CacheInstance CreateInstance(string name, CacheInstanceConfig config, Func<DateTime>? clock = null) {
    lock (this._lock) {
      if (this._instances.ContainsKey(name))
        throw new ConfigValidationException($"Cache instance '{name}' already exists");

      var instance = new CacheInstance(name, config, clock);
      if (this.TotalCommittedBytes + instance.SizeBytes > this.MaxTotalBytes)
        throw new ConfigValidationException($"Cache instance '{name}' ({instance.SizeBytes} bytes) would over-commit the host ceiling of {this.MaxTotalBytes} bytes (SAFE-RAM-BUDGET)");

      this._instances.Add(name, instance);
      return instance;
    }
  }

  public CacheInstance GetInstance(string name) {
    lock (this._lock)
      return this._instances.TryGetValue(name, out var instance)
        ? instance
        : throw new ConfigValidationException($"Unknown cache instance '{name}'");
  }

  /// <summary>Attaches a pool to a named shared instance (weighted) or creates its dedicated instance.</summary>
  public CacheInstance AttachPool(Guid poolId, CacheAttachmentConfig? attachment, Func<DateTime>? clock = null) {
    var config = attachment ?? new CacheAttachmentConfig();
    CacheInstance instance;
    if (config.Dedicated is { } dedicated)
      instance = this.CreateInstance($"dedicated:{poolId:D}", dedicated, clock);
    else
      instance = this.GetInstance(config.Use ?? "global");

    instance.Pages.SetPoolWeight(poolId, config.Weight ?? 1.0);
    lock (this._lock)
      this._attachments[poolId] = instance;

    return instance;
  }

  public CacheInstance GetPoolCache(Guid poolId) {
    lock (this._lock)
      return this._attachments.TryGetValue(poolId, out var instance)
        ? instance
        : throw new ConfigValidationException($"Pool {poolId} is not attached to any cache instance");
  }

}
