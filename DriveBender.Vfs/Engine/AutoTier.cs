using System.Diagnostics;

namespace DivisonM.Vfs.Engine;

/// <summary>
/// Latency-measuring decorator over a member (FR-AUTO-TIER): every storage-touching
/// operation — including the returned streams' reads/writes/flushes — feeds an EWMA of
/// observed latency, so a drive that turns slow or busy becomes visible to the auto-tier
/// advisor without separate benchmarking I/O.
/// </summary>
public sealed class MeasuredVolumeIO(IVolumeIO inner) : IVolumeIO {

  private const double _ALPHA = 0.2; // EWMA smoothing: recent behaviour dominates, spikes decay
  private readonly Lock _lock = new();
  private double _ewmaMs;
  private long _samples;

  public IVolumeIO Inner => inner;

  public double AverageLatencyMs {
    get {
      lock (this._lock)
        return this._ewmaMs;
    }
  }

  public long Samples {
    get {
      lock (this._lock)
        return this._samples;
    }
  }

  private void _Record(double milliseconds) {
    lock (this._lock) {
      this._ewmaMs = this._samples == 0 ? milliseconds : _ALPHA * milliseconds + (1 - _ALPHA) * this._ewmaMs;
      ++this._samples;
    }
  }

  /// <summary>Feeds an observed latency directly — for tests and simulations of slow/busy drives.</summary>
  public void RecordLatency(double milliseconds) => this._Record(milliseconds);

  private T _Timed<T>(Func<T> operation) {
    var watch = Stopwatch.StartNew();
    try {
      return operation();
    } finally {
      this._Record(watch.Elapsed.TotalMilliseconds);
    }
  }

  private void _Timed(Action operation) => this._Timed<object?>(() => {
    operation();
    return null;
  });

  public Guid MemberId => inner.MemberId;
  public string DisplayName => inner.DisplayName;
  public string PhysicalVolumeId => inner.PhysicalVolumeId;
  public bool IsOnline => inner.IsOnline;
  public long BytesFree => inner.BytesFree;
  public long BytesTotal => inner.BytesTotal;
  public BackendCaps Caps => inner.Caps;

  public Stream OpenRead(string relativePath, bool shadow) => new MeasuredStream(this._Timed(() => inner.OpenRead(relativePath, shadow)), this);
  public Stream OpenWrite(string relativePath, bool shadow, bool create) => new MeasuredStream(this._Timed(() => inner.OpenWrite(relativePath, shadow, create)), this);
  public void Truncate(string relativePath, bool shadow, long length) => this._Timed(() => inner.Truncate(relativePath, shadow, length));
  public void Delete(string relativePath, bool shadow) => this._Timed(() => inner.Delete(relativePath, shadow));
  public void EnsureFolder(string relativeFolder, bool shadow) => this._Timed(() => inner.EnsureFolder(relativeFolder, shadow));
  public void DeleteFolder(string relativeFolder, bool shadow) => this._Timed(() => inner.DeleteFolder(relativeFolder, shadow));
  public void RenameFolder(string fromRelativeFolder, string toRelativeFolder) => this._Timed(() => inner.RenameFolder(fromRelativeFolder, toRelativeFolder));
  public void AtomicReplace(string tempRelative, string finalRelative, bool shadow) => this._Timed(() => inner.AtomicReplace(tempRelative, finalRelative, shadow));
  public FileMeta? Stat(string relativePath, bool shadow) => inner.Stat(relativePath, shadow);
  public bool FileExists(string relativePath, bool shadow) => inner.FileExists(relativePath, shadow);
  public bool FolderExists(string relativeFolder, bool shadow) => inner.FolderExists(relativeFolder, shadow);
  public IEnumerable<VolumeEntry> List(string relativeFolder, bool shadow) => inner.List(relativeFolder, shadow);
  public void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc) => inner.SetTimestamps(relativePath, shadow, creationTimeUtc, lastWriteTimeUtc);

  /// <summary>Times the actual data movement — where a busy disk really shows.</summary>
  private sealed class MeasuredStream(Stream inner, MeasuredVolumeIO owner) : Stream {
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) => owner._Timed(() => inner.Read(buffer, offset, count));
    public override void Write(byte[] buffer, int offset, int count) => owner._Timed(() => inner.Write(buffer, offset, count));
    public override void Flush() => owner._Timed(inner.Flush);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing) {
      if (disposing)
        inner.Dispose();
      base.Dispose(disposing);
    }
  }

}

/// <summary>One member's measured speed for the auto-tier decision.</summary>
public sealed record MemberSpeed(Guid MemberId, string DisplayName, double AverageLatencyMs, long Samples, bool Network, MemberRole Role);

/// <summary>The advisor's recommendation: promote one member to landing (demoting the current one, when set).</summary>
public sealed record TierAdvice(Guid PromoteToLanding, Guid? DemoteToCapacity, string Reason);

/// <summary>
/// Auto landing-zone advisor (FR-AUTO-TIER): watches measured member latencies and
/// recommends re-tiering — promote a clearly faster member when no landing zone exists,
/// or swap when the current landing zone has become slow/busy. Hysteresis (a required
/// speed advantage) plus a cooldown keep it from flapping; remote and read-only members
/// are never landing candidates.
/// </summary>
public sealed class AutoTierAdvisor(double requiredSpeedAdvantage = 2.0, long minSamples = 50, TimeSpan? cooldown = null, Func<DateTime>? clock = null) {

  private readonly TimeSpan _cooldown = cooldown ?? TimeSpan.FromMinutes(10);
  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
  private DateTime _lastChangeUtc = DateTime.MinValue;

  public TierAdvice? Advise(IReadOnlyList<MemberSpeed> members) {
    if (this._clock() - this._lastChangeUtc < this._cooldown)
      return null;

    var eligible = members.Where(m => m is { Network: false, Role: not MemberRole.ReadOnly } && m.Samples >= minSamples).ToArray();
    if (eligible.Length < 2)
      return null;

    var fastest = eligible.OrderBy(m => m.AverageLatencyMs).First();
    var landing = eligible.FirstOrDefault(m => m.Role == MemberRole.Landing);

    if (landing == null) {
      // auto-detect: promote only when one member is decisively faster than the median of the rest
      var others = eligible.Where(m => m != fastest).Select(m => m.AverageLatencyMs).OrderBy(x => x).ToArray();
      var median = others[others.Length / 2];
      if (fastest.AverageLatencyMs * requiredSpeedAdvantage > median)
        return null;

      this._lastChangeUtc = this._clock();
      return new(fastest.MemberId, null,
        $"auto landing-zone: '{fastest.DisplayName}' ({fastest.AverageLatencyMs:F1} ms) is ≥{requiredSpeedAdvantage:F0}× faster than the median ({median:F1} ms)");
    }

    // swap only when the current landing zone is decisively slower than the best alternative
    if (fastest.MemberId == landing.MemberId || landing.AverageLatencyMs < fastest.AverageLatencyMs * requiredSpeedAdvantage)
      return null;

    this._lastChangeUtc = this._clock();
    return new(fastest.MemberId, landing.MemberId,
      $"landing zone '{landing.DisplayName}' has become slow/busy ({landing.AverageLatencyMs:F1} ms vs '{fastest.DisplayName}' {fastest.AverageLatencyMs:F1} ms) — swapping");
  }

}
