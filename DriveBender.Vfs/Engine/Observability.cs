namespace DivisonM.Vfs.Engine;

public enum ActivityKind {
  Read,
  Write,
  Drain,
  Duplicate,
  Rebalance,
  RemoteTransfer,
  CacheAdmit,
  CacheEvict,
  Recovery,
  Scrub,
  TrashMove,
}

/// <summary>One in-flight or completed operation for the live activity view (OPS-EVENTS, FR-UI-MAP).</summary>
public sealed record ActivityEvent(
  ActivityKind Kind,
  string Path,
  long Bytes,
  string? FromMember,
  string? ToMember,
  string Reason,
  DateTime TimestampUtc
);

/// <summary>Point-in-time engine counters for the dashboard (OPS-METRICS).</summary>
public sealed record PoolMetrics(
  long ReadBytes,
  long WrittenBytes,
  long CacheHits,
  long CacheMisses,
  long DirtyFiles,
  long DrainedFiles,
  long RecoveredOperations,
  long IntegrityIssues
) {
  public double CacheHitRate => this.CacheHits + this.CacheMisses == 0 ? 0 : (double)this.CacheHits / (this.CacheHits + this.CacheMisses);
}

/// <summary>
/// Sampled, rate-limited activity feed plus counters (OPS-EVENTS): subscribers get a
/// best-effort stream — under load samples are dropped rather than blocking I/O
/// (NFR-UI-LIVE), and the ring buffer keeps a short rolling history for playback.
/// </summary>
public sealed class ActivityFeed(int ringCapacity = 512, int maxEventsPerSecond = 200, Func<DateTime>? clock = null) {

  private readonly Queue<ActivityEvent> _ring = new(ringCapacity);
  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
  private readonly Lock _lock = new();
  private DateTime _windowStart;
  private int _eventsThisWindow;

  private long _readBytes;
  private long _writtenBytes;
  private long _drainedFiles;
  private long _recoveredOperations;
  private long _integrityIssues;

  public event Action<ActivityEvent>? EventPublished;

  public long DroppedSamples { get; private set; }

  /// <summary>Publishes an event unless the rate limit says drop — never blocks the I/O path.</summary>
  public void Publish(ActivityKind kind, string path, long bytes = 0, string? fromMember = null, string? toMember = null, string reason = "") {
    switch (kind) {
      case ActivityKind.Read:
        Interlocked.Add(ref this._readBytes, bytes);
        break;
      case ActivityKind.Write:
        Interlocked.Add(ref this._writtenBytes, bytes);
        break;
      case ActivityKind.Drain:
        Interlocked.Increment(ref this._drainedFiles);
        break;
      case ActivityKind.Recovery:
        Interlocked.Increment(ref this._recoveredOperations);
        break;
      case ActivityKind.Scrub:
        Interlocked.Increment(ref this._integrityIssues);
        break;
    }

    ActivityEvent? published = null;
    lock (this._lock) {
      var now = this._clock();
      if (now - this._windowStart >= TimeSpan.FromSeconds(1)) {
        this._windowStart = now;
        this._eventsThisWindow = 0;
      }

      if (++this._eventsThisWindow > maxEventsPerSecond) {
        ++this.DroppedSamples; // best-effort feed: drop, never block (OPS-EVENTS)
        return;
      }

      published = new(kind, path, bytes, fromMember, toMember, reason, now);
      if (this._ring.Count >= ringCapacity)
        this._ring.Dequeue();

      this._ring.Enqueue(published);
    }

    this.EventPublished?.Invoke(published);
  }

  /// <summary>Rolling history so a burst can be reviewed after it happened (FR-UI-MAP playback).</summary>
  public IReadOnlyList<ActivityEvent> History {
    get {
      lock (this._lock)
        return [.. this._ring];
    }
  }

  public PoolMetrics Snapshot(Caching.CacheStatistics cacheStatistics, int dirtyFiles) => new(
    Interlocked.Read(ref this._readBytes),
    Interlocked.Read(ref this._writtenBytes),
    cacheStatistics.Hits,
    cacheStatistics.Misses,
    dirtyFiles,
    Interlocked.Read(ref this._drainedFiles),
    Interlocked.Read(ref this._recoveredOperations),
    Interlocked.Read(ref this._integrityIssues)
  );

}
