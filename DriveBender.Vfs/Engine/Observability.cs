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
  private long _windowStartTicks;
  private int _eventsThisWindow;

  private long _readBytes;
  private long _writtenBytes;
  private long _drainedFiles;
  private long _recoveredOperations;
  private long _integrityIssues;

  public event Action<ActivityEvent>? EventPublished;

  public long DroppedSamples => Interlocked.Read(ref this._droppedSamples);

  private long _droppedSamples;

  /// <summary>
  /// Publishes an event unless the rate limit says drop — never blocks the I/O path.
  ///
  /// EVERY read and write publishes here, so the rate-limit decision is taken WITHOUT the lock:
  /// once a busy second has spent its budget (the normal state under load — a pool doing
  /// thousands of I/Os per second against a 200/s feed) the call returns having touched nothing
  /// but two interlocked counters. Taking the lock first made this one process-wide contention
  /// point on the hot path of every file operation, purely to decide the event would be dropped.
  /// </summary>
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

    var now = this._clock();

    // roll the rate window; whichever thread wins the exchange resets the budget, the rest simply
    // spend against the new one — an event either side of the boundary is not worth a lock
    var windowStart = Interlocked.Read(ref this._windowStartTicks);
    if (now.Ticks - windowStart >= TimeSpan.TicksPerSecond
        && Interlocked.CompareExchange(ref this._windowStartTicks, now.Ticks, windowStart) == windowStart)
      Interlocked.Exchange(ref this._eventsThisWindow, 0);

    if (Interlocked.Increment(ref this._eventsThisWindow) > maxEventsPerSecond) {
      Interlocked.Increment(ref this._droppedSamples); // best-effort feed: drop, never block (OPS-EVENTS)
      return;
    }

    var published = new ActivityEvent(kind, path, bytes, fromMember, toMember, reason, now);
    lock (this._lock) {
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
