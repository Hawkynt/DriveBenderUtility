using DivisonM.Vfs;

namespace DivisonM.Vfs.Tests.TestSupport;

/// <summary>
/// Wraps a member and records HOW MANY of its reads are in flight at the same time.
///
/// Every other assertion about the I/O path can be made on the bytes that came back; this one
/// cannot. "The engine keeps the storage busy" is a statement about overlap, and overlap is
/// invisible to a test that only looks at results — a perfectly serial implementation returns
/// exactly the same bytes. So the member itself is asked, and given an optional delay per read,
/// because on an in-memory fake a correct fan-out would otherwise complete each block long
/// before the next one started and the peak would read as one for entirely the wrong reason.
/// </summary>
public sealed class ProbeVolumeIO(IVolumeIO inner, TimeSpan readDelay = default) : IVolumeIO {

  private int _inFlight;
  private int _peakConcurrentReads;
  private int _reads;

  /// <summary>The most reads this member ever had open at one moment.</summary>
  public int PeakConcurrentReads => Volatile.Read(ref this._peakConcurrentReads);

  /// <summary>How many reads were ATTEMPTED, failures included — what a dying disk costs.</summary>
  public int ReadAttempts => Volatile.Read(ref this._reads);

  public void ResetCounters() {
    Volatile.Write(ref this._peakConcurrentReads, 0);
    Volatile.Write(ref this._reads, 0);
  }

  public IVolumeIO Inner => inner;

  public Guid MemberId => inner.MemberId;
  public string DisplayName => inner.DisplayName;
  public string PhysicalVolumeId => inner.PhysicalVolumeId;
  public bool IsOnline => inner.IsOnline;
  public long BytesFree => inner.BytesFree;
  public long BytesTotal => inner.BytesTotal;
  public BackendCaps Caps => inner.Caps;

  public Stream OpenRead(string relativePath, bool shadow) {
    Interlocked.Increment(ref this._reads);
    var live = Interlocked.Increment(ref this._inFlight);
    _RaiseTo(ref this._peakConcurrentReads, live);
    try {
      if (readDelay > TimeSpan.Zero)
        Thread.Sleep(readDelay);

      return new CountingStream(inner.OpenRead(relativePath, shadow), this);
    } catch (Exception) {
      Interlocked.Decrement(ref this._inFlight);
      throw;
    }
  }

  private static void _RaiseTo(ref int target, int candidate) {
    while (true) {
      var current = Volatile.Read(ref target);
      if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
        return;
    }
  }

  public Stream OpenWrite(string relativePath, bool shadow, bool create) => inner.OpenWrite(relativePath, shadow, create);
  public void Truncate(string relativePath, bool shadow, long length) => inner.Truncate(relativePath, shadow, length);
  public void Delete(string relativePath, bool shadow) => inner.Delete(relativePath, shadow);
  public void EnsureFolder(string relativeFolder, bool shadow) => inner.EnsureFolder(relativeFolder, shadow);
  public void DeleteFolder(string relativeFolder, bool shadow) => inner.DeleteFolder(relativeFolder, shadow);
  public void RenameFolder(string from, string to) => inner.RenameFolder(from, to);
  public void AtomicReplace(string tempRelative, string finalRelative, bool shadow) => inner.AtomicReplace(tempRelative, finalRelative, shadow);
  public FileMeta? Stat(string relativePath, bool shadow) => inner.Stat(relativePath, shadow);
  public bool FileExists(string relativePath, bool shadow) => inner.FileExists(relativePath, shadow);
  public bool FolderExists(string relativeFolder, bool shadow) => inner.FolderExists(relativeFolder, shadow);
  public IEnumerable<VolumeEntry> List(string relativeFolder, bool shadow) => inner.List(relativeFolder, shadow);
  public void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc)
    => inner.SetTimestamps(relativePath, shadow, creationTimeUtc, lastWriteTimeUtc);

  /// <summary>Holds the member's "a read is open" count until the engine actually lets the stream go.</summary>
  private sealed class CountingStream(Stream inner, ProbeVolumeIO owner) : Stream {
    private int _closed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing) {
      if (disposing && Interlocked.Exchange(ref this._closed, 1) == 0) {
        inner.Dispose();
        Interlocked.Decrement(ref owner._inFlight);
      }

      base.Dispose(disposing);
    }
  }

}
