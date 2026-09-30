using System.Diagnostics;

namespace DivisonM.Vfs.Engine;

/// <summary>
/// Latency-measuring decorator over a member: every storage-touching operation — including the
/// returned streams' reads/writes/flushes — feeds an EWMA of observed latency, so a drive that
/// turns slow or busy becomes visible to placement, read routing and the dashboard without
/// separate benchmarking I/O.
/// </summary>
public sealed class MeasuredVolumeIO(IVolumeIO inner, TimeProvider? time = null) : IVolumeIO {

  private const double _ALPHA = 0.2; // EWMA smoothing: recent behaviour dominates, spikes decay
  private readonly Lock _lock = new();
  private readonly TimeProvider _time = time ?? TimeProvider.System;

  // One average PER KIND of operation, reported as the mean of the kinds seen. A single average
  // was a function of the workload's MIX as much as of the disk: a disk that takes 3 ms per write
  // and per flush but answers a rename or a truncate at once reads close to the fast one when its
  // recent work happened to be mostly renames. Striping shifted exactly that mix — a final disk now
  // does a few long fill writes and a tail of quick metadata — and on a CI runner such a disk sank
  // under the 1 ms floor, tied with the fast one, and took half the files. Averaged per kind, a disk
  // slow at moving data stays slow however many quick metadata operations it also does.
  private const int _KINDS = 3;
  private const int _DATA = 0;     // reads and writes of file content
  private const int _FLUSH = 1;    // durability barriers
  private const int _METADATA = 2; // open, delete, rename, truncate, folder operations
  private readonly double[] _ewmaMs = new double[_KINDS];
  private readonly long[] _samplesOf = new long[_KINDS];
  private long _samples;
  private long _lastSampleTimestamp;

  public IVolumeIO Inner => inner;

  public double AverageLatencyMs {
    get {
      lock (this._lock) {
        var sum = 0.0;
        var kinds = 0;
        for (var kind = 0; kind < _KINDS; ++kind)
          if (this._samplesOf[kind] > 0) {
            sum += this._ewmaMs[kind];
            ++kinds;
          }

        return kinds == 0 ? 0 : sum / kinds;
      }
    }
  }

  public long Samples {
    get {
      lock (this._lock)
        return this._samples;
    }
  }

  /// <summary>
  /// How long ago the average last moved — <see cref="TimeSpan.MaxValue"/> when never measured.
  /// An average only changes when the member is USED, so one nobody chooses keeps whatever it last
  /// saw, however long ago and however unrepresentative; a decision that leans on it has to know.
  /// </summary>
  public TimeSpan SinceLastSample {
    get {
      lock (this._lock)
        return this._samples == 0 ? TimeSpan.MaxValue : this._time.GetElapsedTime(this._lastSampleTimestamp);
    }
  }

  private void _Record(int kind, double milliseconds) {
    lock (this._lock) {
      this._ewmaMs[kind] = this._samplesOf[kind] == 0 ? milliseconds : _ALPHA * milliseconds + (1 - _ALPHA) * this._ewmaMs[kind];
      ++this._samplesOf[kind];
      ++this._samples;
      this._lastSampleTimestamp = this._time.GetTimestamp();
    }
  }

  /// <summary>Feeds an observed data-transfer latency directly — for tests and simulations of slow/busy drives.</summary>
  public void RecordLatency(double milliseconds) => this._Record(_DATA, milliseconds);

  /// <summary>Feeds an observed metadata-operation latency directly — for tests.</summary>
  public void RecordMetadataLatency(double milliseconds) => this._Record(_METADATA, milliseconds);

  private T _Timed<T>(int kind, Func<T> operation) {
    var watch = Stopwatch.StartNew();
    try {
      return operation();
    } finally {
      this._Record(kind, watch.Elapsed.TotalMilliseconds);
    }
  }

  private void _Timed(int kind, Action operation) => this._Timed<object?>(kind, () => {
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
  public bool IsCaseSensitive => inner.IsCaseSensitive; // a decorator must never answer for its member

  public Stream OpenRead(string relativePath, bool shadow) => new MeasuredStream(this._Timed(_METADATA, () => inner.OpenRead(relativePath, shadow)), this);
  public Stream OpenWrite(string relativePath, bool shadow, bool create) => new MeasuredStream(this._Timed(_METADATA, () => inner.OpenWrite(relativePath, shadow, create)), this);
  public void Truncate(string relativePath, bool shadow, long length) => this._Timed(_METADATA, () => inner.Truncate(relativePath, shadow, length));
  public void Delete(string relativePath, bool shadow) => this._Timed(_METADATA, () => inner.Delete(relativePath, shadow));
  public void EnsureFolder(string relativeFolder, bool shadow) => this._Timed(_METADATA, () => inner.EnsureFolder(relativeFolder, shadow));
  public void DeleteFolder(string relativeFolder, bool shadow) => this._Timed(_METADATA, () => inner.DeleteFolder(relativeFolder, shadow));
  public void RenameFolder(string fromRelativeFolder, string toRelativeFolder) => this._Timed(_METADATA, () => inner.RenameFolder(fromRelativeFolder, toRelativeFolder));
  public void AtomicReplace(string tempRelative, string finalRelative, bool shadow) => this._Timed(_METADATA, () => inner.AtomicReplace(tempRelative, finalRelative, shadow));
  public FileMeta? Stat(string relativePath, bool shadow) => inner.Stat(relativePath, shadow);
  public bool FileExists(string relativePath, bool shadow) => inner.FileExists(relativePath, shadow);
  public bool FolderExists(string relativeFolder, bool shadow) => inner.FolderExists(relativeFolder, shadow);
  public IEnumerable<VolumeEntry> List(string relativeFolder, bool shadow) => inner.List(relativeFolder, shadow);
  public void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc) => inner.SetTimestamps(relativePath, shadow, creationTimeUtc, lastWriteTimeUtc);

  // Forwarded EXPLICITLY, like everything else here. This one has a default implementation on the
  // interface — a no-op, so that a backend with no notion of permissions is unaffected — and a
  // decorator that forwards Caps while inheriting that default is the worst of both: the member
  // advertises that it keeps a mode and then quietly discards every one it is given. Which is
  // exactly what happened, and it looked like the chmod never arriving.
  public void SetPermissions(string relativePath, bool shadow, UnixFileMode mode) => inner.SetPermissions(relativePath, shadow, mode);

  // same trap as SetPermissions: inheriting the no-op default would drop every folder stamp
  public void SetFolderTimestamps(string relativeFolder, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc) => inner.SetFolderTimestamps(relativeFolder, creationTimeUtc, lastWriteTimeUtc);

  // forwarded explicitly: an inherited default would report a shared file as unshared
  public int LinkCount(string relativePath, bool shadow) => inner.LinkCount(relativePath, shadow);
  public bool TryHardLink(string existingRelative, bool existingShadow, string newRelative, bool newShadow) => inner.TryHardLink(existingRelative, existingShadow, newRelative, newShadow);
  public bool TryClone(string sourceRelative, bool sourceShadow, string targetRelative, bool targetShadow) => inner.TryClone(sourceRelative, sourceShadow, targetRelative, targetShadow);
  public bool TryPunchHole(string relativePath, bool shadow, long offset, long length) => inner.TryPunchHole(relativePath, shadow, offset, length);
  public long AllocatedBytes(string relativePath, bool shadow) => inner.AllocatedBytes(relativePath, shadow);
  public IDisposable? WatchChanges(Action<string?> changed) => inner.WatchChanges(changed);

  /// <summary>Times the actual data movement — where a busy disk really shows.</summary>
  private sealed class MeasuredStream(Stream inner, MeasuredVolumeIO owner) : Stream {
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) => owner._Timed(_DATA, () => inner.Read(buffer, offset, count));
    public override void Write(byte[] buffer, int offset, int count) => owner._Timed(_DATA, () => inner.Write(buffer, offset, count));

    // The span overloads must be forwarded too, not inherited. Stream's base versions serve a span
    // caller by renting an array, copying through it and returning it — so a wrapper that forwards
    // only the array overloads silently reintroduces the copy for every span caller, and
    // Stream.CopyTo is one. Forwarding keeps the caller's own memory all the way to the member.
    public override int Read(Span<byte> buffer) {
      // timed inline rather than through _Timed: a Span is a ref struct and cannot be captured by
      // the lambda that helper takes
      var clock = System.Diagnostics.Stopwatch.StartNew();
      try {
        return inner.Read(buffer);
      } finally {
        owner.RecordLatency(clock.Elapsed.TotalMilliseconds);
      }
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      var clock = System.Diagnostics.Stopwatch.StartNew();
      try {
        inner.Write(buffer);
      } finally {
        owner.RecordLatency(clock.Elapsed.TotalMilliseconds);
      }
    }
    public override void Flush() => owner._Timed(_FLUSH, inner.Flush);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing) {
      if (disposing)
        inner.Dispose();
      base.Dispose(disposing);
    }
  }

}
