using DivisonM.Vfs;
using Hawkynt.CloudStorage;

namespace DivisonM.Backends;

public readonly record struct StoreEntry(string Name, bool IsFolder, long Length, DateTime ModifiedUtc);

/// <summary>What a remote store can do natively, as opposed to what the interface's defaults emulate.</summary>
[Flags]
public enum StoreCaps {
  None = 0,

  /// <summary>Serves a byte range without transferring the whole object.</summary>
  RangeRead = 1 << 0,

  /// <summary>Consumes an upload from a stream, so a large file is never materialised in memory.</summary>
  StreamingUpload = 1 << 1,

  /// <summary>Hands back a readable stream rather than a completed array.</summary>
  StreamingDownload = 1 << 2,
}

public sealed record StoreMeta(bool IsFolder, long Length, DateTime CreatedUtc, DateTime ModifiedUtc);

/// <summary>
/// The minimal contract a remote endpoint must offer to join a pool: whole-object
/// download/upload plus namespace primitives. Each provider implements this directly
/// against its official SDK — no wrapper libraries. Paths are pool-physical, slash
/// separated, never rooted.
/// </summary>
public interface IWholeFileStore : IDisposable {

  /// <summary>Establishes the connection when the protocol needs one; idempotent.</summary>
  void Connect();

  /// <summary>Cheap reachability check for the online probe.</summary>
  bool Probe();

  byte[] Download(string physicalPath);

  /// <summary>Uploads the whole object, overwriting; parent folders are guaranteed to exist beforehand.</summary>
  void Upload(string physicalPath, byte[] content);

  /// <summary>
  /// What the underlying provider does NATIVELY. Every operation below works on every store —
  /// the defaults emulate them with whole-object transfers — so this is about cost, not
  /// capability, and the engine uses it to choose a read strategy.
  /// </summary>
  StoreCaps Caps => StoreCaps.None;

  /// <summary>Opens the object for reading; the default materialises it first.</summary>
  Stream OpenRead(string physicalPath) => new MemoryStream(this.Download(physicalPath), false);

  /// <summary>
  /// Reads a byte range. With <see cref="StoreCaps.RangeRead"/> this is a real ranged request and
  /// costs the range; without it the whole object moves and is sliced, which is precisely the
  /// difference the capability exists to express.
  /// </summary>
  Stream OpenReadRange(string physicalPath, long offset, long count) {
    var whole = this.Download(physicalPath);
    if (offset >= whole.LongLength || count <= 0)
      return new MemoryStream([], false);

    return new MemoryStream(whole, (int)offset, (int)Math.Min(count, whole.LongLength - offset), false);
  }

  /// <summary>Uploads from a stream; the default buffers it whole, which is what <see cref="StoreCaps.StreamingUpload"/> avoids.</summary>
  void Upload(string physicalPath, Stream content, long length = -1) {
    using var buffer = length is > 0 and <= int.MaxValue ? new MemoryStream((int)length) : new MemoryStream();
    content.CopyTo(buffer);
    this.Upload(physicalPath, buffer.ToArray());
  }

  void DeleteFile(string physicalPath);

  /// <summary>File or folder metadata, null when nothing exists at the path.</summary>
  StoreMeta? Stat(string physicalPath);

  /// <summary>Creates one folder level; parents are guaranteed to exist.</summary>
  void CreateFolder(string physicalPath);

  void DeleteFolder(string physicalPath);

  IEnumerable<StoreEntry> List(string physicalFolder);

  /// <summary>
  /// True when concurrent calls are safe. Default FALSE (correctness first): single-connection
  /// protocols like FTP and SFTP have ONE control channel, so the engine's parallel prefetch /
  /// mirror-split reads would interleave commands and corrupt the session. A store that is safe
  /// for concurrent use (independent requests, e.g. a local directory or an HTTP-per-request SDK)
  /// overrides this to true so it is not needlessly serialized.
  /// </summary>
  bool ThreadSafe => false;

}

/// <summary>
/// Adapts any <see cref="IWholeFileStore"/> into a whole-file <see cref="IVolumeIO"/>
/// member (§6.1). Reads go through provider RANGE requests where the store supports them, so
/// the engine's block-by-block reads transfer their blocks and nothing more; where it does not,
/// they fall back to buffering the whole object behind a bounded cache. Writes stage in memory
/// and stream out on flush (read-modify-write for positional writes). The capability set
/// carries neither <see cref="BackendCaps.AtomicRename"/> nor
/// <see cref="BackendCaps.DurableFlush"/>, so the engine journals around the gaps and
/// never counts such a member toward the ack quorum (FR-CAP-ADAPT, SAFE-REMOTE).
/// </summary>
public sealed class WholeFileVolumeIO(Guid memberId, string displayName, string physicalVolumeId, IWholeFileStore store, Func<DateTime>? clock = null) : IVolumeIO, IDisposable {

  private static readonly TimeSpan _PROBE_TTL = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long a NEGATIVE probe is trusted. A healthy member is trusted for the full TTL — there
  /// is no point re-probing something that just answered. A member believed down is re-checked
  /// far sooner: writing a member off for thirty seconds because of one dropped packet turns a
  /// blip into a degraded pool, and every read of its copies goes to another member meanwhile.
  /// </summary>
  private static readonly TimeSpan _PROBE_TTL_OFFLINE = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Whole-object backends buffer the entire file in a single <c>byte[]</c>/<c>MemoryStream</c>,
  /// which caps at <see cref="int.MaxValue"/>. Rather than silently truncate a larger file
  /// (an <c>(int)</c> cast wraps), such a member REFUSES it with NoSpace — placement then keeps
  /// the file on a local member that can hold it (SAFE-BIGFILE). Local/UNC members have no such
  /// cap (positional I/O), so files far larger than this are fully supported there.
  /// </summary>
  internal const long MaxFileSize = int.MaxValue;

  // serialize a non-thread-safe store (FTP/SFTP: one control channel) so the engine's parallel
  // prefetch / mirror-split reads never interleave commands and corrupt the session; a store that
  // declares itself thread-safe is used directly (no lock overhead)
  private readonly IWholeFileStore _store = store.ThreadSafe ? store : new SerializingWholeFileStore(store);

  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);

  // Probe state is read from the hot path (ResolveCopies filters every copy by IsOnline) and
  // written by whichever caller happens to find it expired, so it must be published atomically.
  // A LOCK here would park every concurrent caller behind one network round trip; instead a
  // single-prober gate lets one caller refresh while the rest are served the last known answer.
  private long _lastProbeTicks;
  private volatile bool _lastProbeResult;
  private int _probing;

  private readonly Lock _connectLock = new();
  private volatile bool _connected;

  // The FALLBACK read path, for a provider that cannot serve ranges. The engine reads a file BLOCK
  // BY BLOCK, and without ranges each block miss re-downloads the WHOLE object (a cold 1 GiB read
  // in 128 KiB blocks would move ~8000× the file). A bounded LRU of recently-downloaded objects
  // collapses that burst back to ONE download per file (O(n²)→O(n)) — still a whole-object
  // transfer to serve one block, which is exactly what StoreCaps.RangeRead avoids.
  // Small objects share a budget; one over-budget object is allowed to occupy the cache alone so a
  // large sequential read still reuses a single download (bounded to one big object at a time —
  // whole-object remotes already refuse files past int.MaxValue). A short TTL bounds staleness from
  // out-of-band changes, and every write invalidates its own entry.
  private const long _READ_CACHE_BUDGET = 256L * 1024 * 1024;
  private static readonly TimeSpan _READ_CACHE_TTL = TimeSpan.FromSeconds(30);

  private readonly Lock _readCacheLock = new();
  private readonly LinkedList<string> _readCacheOrder = new();
  private readonly Dictionary<string, (byte[] content, DateTime at, LinkedListNode<string> node)> _readCache = new(StringComparer.OrdinalIgnoreCase);
  private long _readCacheBytes;

  private byte[] _CachedDownload(string physical) {
    lock (this._readCacheLock) {
      if (this._readCache.TryGetValue(physical, out var hit) && this._clock() - hit.at < _READ_CACHE_TTL) {
        this._readCacheOrder.Remove(hit.node);
        this._readCacheOrder.AddFirst(hit.node);
        return hit.content;
      }
    }

    var content = this._store.Download(physical); // the network fetch happens OUTSIDE the lock
    lock (this._readCacheLock) {
      this._InvalidateReadCacheLocked(physical);
      var node = new LinkedListNode<string>(physical);
      this._readCacheOrder.AddFirst(node);
      this._readCache[physical] = (content, this._clock(), node);
      this._readCacheBytes += content.Length;
      // evict LRU while over budget, but always keep at least the just-added entry so ONE large
      // object can be held alone (a big sequential read reuses its single download)
      while (this._readCacheBytes > _READ_CACHE_BUDGET && this._readCacheOrder.Count > 1)
        this._InvalidateReadCacheLocked(this._readCacheOrder.Last!.Value);
    }

    return content;
  }

  private void _InvalidateReadCache(string physical) {
    lock (this._readCacheLock)
      this._InvalidateReadCacheLocked(physical);
  }

  private void _InvalidateReadCacheLocked(string physical) {
    if (this._readCache.Remove(physical, out var entry)) {
      this._readCacheOrder.Remove(entry.node);
      this._readCacheBytes -= entry.content.Length;
    }
  }

  private void _ClearReadCache() {
    lock (this._readCacheLock) {
      this._readCache.Clear();
      this._readCacheOrder.Clear();
      this._readCacheBytes = 0;
    }
  }

  public Guid MemberId { get; } = memberId;
  public string DisplayName { get; } = displayName;
  public string PhysicalVolumeId { get; } = physicalVolumeId;

  /// <summary>Whole-file capacity/archive tier: no atomic rename, no durable flush, no timestamp writes (§6.1 capability table).</summary>
  public BackendCaps Caps => BackendCaps.List | BackendCaps.Delete | BackendCaps.ServerCredentials;

  /// <summary>Capacity is unreported by most remote services: BytesTotal 0 means "unknown — excluded from pool aggregates" (FR-STAT convention).</summary>
  public long BytesTotal => 0;

  /// <summary>Placement sentinel: a remote capacity tier is assumed to have room; real limits surface as NoSpace on upload.</summary>
  public long BytesFree => long.MaxValue / 2;

  /// <summary>
  /// Every operation here is a network round trip on the calling thread — the provider SDKs are
  /// driven sync-over-async because the driver callbacks (WinFsp/Dokan/FUSE) are synchronous. The
  /// engine uses this to keep such work off the shared thread pool (see BlockingIoScheduler).
  /// </summary>
  public bool BlocksCallingThread => true;

  public bool IsOnline {
    get {
      var now = this._clock();
      var lastResult = this._lastProbeResult;
      if (now.Ticks - Interlocked.Read(ref this._lastProbeTicks) < (lastResult ? _PROBE_TTL : _PROBE_TTL_OFFLINE).Ticks)
        return lastResult;

      // one prober at a time: the others keep the last answer rather than piling identical
      // round trips onto a remote that may already be struggling
      if (Interlocked.Exchange(ref this._probing, 1) != 0)
        return this._lastProbeResult;

      try {
        bool probed;
        try {
          this._EnsureConnected();
          probed = this._store.Probe();
        } catch (Exception) {
          probed = false;
        }

        this._lastProbeResult = probed;
        Interlocked.Exchange(ref this._lastProbeTicks, now.Ticks); // published LAST — readers gate on it
        return probed;
      } finally {
        Interlocked.Exchange(ref this._probing, 0);
      }
    }
  }

  /// <summary>
  /// Records that this member just failed to answer. The result is UNKNOWN, never "absent", so
  /// the member is reported offline until it proves otherwise — the engine already models an
  /// unreachable member safely (tombstoned namespace changes, heal on return), whereas it has no
  /// defence against a reachable member that lies about what it holds. The next call re-probes
  /// immediately, so a momentary blip costs one operation rather than a whole TTL.
  /// </summary>
  private void _MarkUnreachable() {
    this._lastProbeResult = false;
    this._connected = false; // the session is probably dead; reconnect before trusting it again
    Interlocked.Exchange(ref this._lastProbeTicks, 0);
  }

  private void _EnsureConnected() {
    if (this._connected)
      return;

    // double-checked: Connect() on a thread-safe store can otherwise run concurrently
    lock (this._connectLock) {
      if (this._connected)
        return;

      this._store.Connect();
      this._connected = true;
    }
  }

  private T _Guard<T>(Func<T> operation) {
    try {
      this._EnsureConnected();
      return operation();
    } catch (Exception e) {
      throw Translate(e, this.DisplayName);
    }
  }

  private void _Guard(Action operation) => this._Guard<object?>(() => {
    operation();
    return null;
  });

  internal static Exception Translate(Exception e, string member) => e switch {
    PoolFsException => e,
    CloudStorageException cloud => cloud.ToPoolFs(member),
    AggregateException { InnerException: not null } aggregate => Translate(aggregate.InnerException!, member),
    FileNotFoundException or DirectoryNotFoundException => new PoolFsException(PoolFsError.NotFound, $"{e.Message} (member '{member}')", e),
    UnauthorizedAccessException => new PoolFsException(PoolFsError.AccessDenied, $"{e.Message} (member '{member}')", e),
    TimeoutException or TaskCanceledException or OperationCanceledException => new PoolFsException(PoolFsError.Offline, $"Member '{member}' timed out: {e.Message}", e),
    System.Net.Sockets.SocketException or HttpRequestException or IOException => new PoolFsException(PoolFsError.Offline, $"Member '{member}' unreachable: {e.Message}", e),
    _ => new PoolFsException(PoolFsError.IoError, $"{e.Message} (member '{member}')", e),
  };

  private static string _File(string relativePath, bool shadow) => PoolPaths.ToPhysical(relativePath, shadow);
  private static string _Folder(string relativeFolder, bool shadow) => PoolPaths.ToPhysicalFolder(relativeFolder, shadow);

  public Stream OpenRead(string relativePath, bool shadow) => this._Guard<Stream>(() => {
    var physical = _File(relativePath, shadow);
    if (this._store.Stat(physical) is not { IsFolder: false, Length: var size })
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

    // A provider that serves ranges gets a windowed reader: the engine reads a file block by
    // block, so it fetches those blocks and nothing else. Without ranges the only way to answer a
    // block is to move the whole object, and the bounded object cache is what stops that being
    // once PER BLOCK — but it still means a 2 GiB read to serve 128 KiB, which is why a member
    // whose provider lacks ranges belongs on whole-file work rather than random reads.
    if ((this._store.Caps & StoreCaps.RangeRead) != 0)
      return new RangeReadStream(this, physical, size);

    return new MemoryStream(this._CachedDownload(physical), writable: false);
  });

  /// <summary>
  /// A seekable read over a remote object, served by RANGE requests rather than by downloading it.
  ///
  /// Requests are rounded out to a window so a run of sequential block reads costs one request per
  /// window instead of one per block, and the window is reused while the caller reads forward
  /// through it. Only the window is ever in memory, so object size is bounded by the service, not
  /// by RAM — the point of the whole exercise.
  /// </summary>
  private sealed class RangeReadStream(WholeFileVolumeIO owner, string physical, long length) : Stream {

    /// <summary>Large enough to amortise a round trip over many engine blocks, small enough to stay off the LOH.</summary>
    private const int _WINDOW = 512 * 1024;

    private byte[] _window = [];
    private long _windowStart = -1;
    private int _windowLength;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position {
      get => this._position;
      set => this._position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => this._position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      if (this._position >= length || buffer.Length == 0)
        return 0;

      var wanted = (int)Math.Min(buffer.Length, length - this._position);
      if (!this._InWindow(this._position))
        this._FillWindow(this._position);

      var offsetInWindow = (int)(this._position - this._windowStart);
      var available = Math.Min(wanted, this._windowLength - offsetInWindow);
      if (available <= 0)
        return 0; // the service returned short — the caller sees a short read, as it would locally

      this._window.AsSpan(offsetInWindow, available).CopyTo(buffer);
      this._position += available;
      return available;
    }

    private bool _InWindow(long position)
      => this._windowStart >= 0 && position >= this._windowStart && position < this._windowStart + this._windowLength;

    private void _FillWindow(long from) {
      var count = (int)Math.Min(_WINDOW, length - from);
      using var range = owner._Guard(() => owner._store.OpenReadRange(physical, from, count));
      if (this._window.Length < count)
        this._window = new byte[count];

      var filled = 0;
      while (filled < count) {
        var read = range.Read(this._window, filled, count - filled);
        if (read <= 0)
          break;

        filled += read;
      }

      this._windowStart = from;
      this._windowLength = filled;
    }

    public override void Flush() {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  public Stream OpenWrite(string relativePath, bool shadow, bool create) => this._Guard<Stream>(() => {
    var physical = _File(relativePath, shadow);
    var exists = this._store.Stat(physical) is { IsFolder: false };
    if (!exists && !create)
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

    if (!exists)
      this._EnsureFolderRecursive(PoolPaths.GetParent(physical));

    // read-modify-write staging: existing content composes with positional writes — but it is
    // loaded LAZILY. A publish (WholeFilePublisher) opens the object and immediately calls
    // SetLength(0); eagerly downloading first meant every heal/drain/publish onto an existing
    // remote file paid a full download purely to throw it away. A brand-new file starts dirty
    // so even an empty create uploads on flush (the cached array is never mutated in place).
    return new UploadOnFlushStream(this, physical, exists ? () => this._CachedDownload(physical) : null, startDirty: !exists);
  });

  private void _Upload(string physical, byte[] content) => this._Guard(() => {
    this._store.Upload(physical, content);
    this._InvalidateReadCache(physical); // the object changed — drop any stale cached copy
  });

  private void _UploadStream(string physical, Stream content, long length) => this._Guard(() => {
    this._store.Upload(physical, content, length);
    this._InvalidateReadCache(physical);
  });

  /// <summary>
  /// Staging stream: mutations happen in memory; Flush uploads the whole object (whole-file
  /// model, §6.1). The existing object is fetched only when an operation could actually observe
  /// it — a <c>SetLength(0)</c> arriving first (the publish pattern) discards it unread, so no
  /// download happens at all.
  /// </summary>
  private sealed class UploadOnFlushStream : MemoryStream {

    private readonly WholeFileVolumeIO _owner;
    private readonly string _physical;
    private Func<byte[]>? _loadInitial; // null once loaded (or once proven unnecessary)
    private bool _dirty;

    public UploadOnFlushStream(WholeFileVolumeIO owner, string physical, Func<byte[]>? loadInitial, bool startDirty) {
      this._owner = owner;
      this._physical = physical;
      this._loadInitial = loadInitial;
      this._dirty = startDirty;
    }

    /// <summary>Pulls the existing object in before anything can observe (or overwrite part of) it.</summary>
    private void _Materialise() {
      var load = Interlocked.Exchange(ref this._loadInitial, null);
      if (load == null)
        return;

      var initial = load();
      if (initial.Length == 0)
        return;

      var position = this.Position;
      this.Position = 0;
      base.Write(initial, 0, initial.Length);
      this.Position = position;
    }

    public override long Length {
      get {
        this._Materialise();
        return base.Length;
      }
    }

    public override int Read(byte[] buffer, int offset, int count) {
      this._Materialise();
      return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer) {
      this._Materialise();
      return base.Read(buffer);
    }

    public override int ReadByte() {
      this._Materialise();
      return base.ReadByte();
    }

    public override long Seek(long offset, SeekOrigin origin) {
      if (origin == SeekOrigin.End)
        this._Materialise(); // relative to the existing length

      return base.Seek(offset, origin);
    }

    public override void Write(byte[] buffer, int offset, int count) {
      if (this.Position + count > MaxFileSize)
        throw new PoolFsException(PoolFsError.NoSpace, $"File exceeds the {MaxFileSize} byte whole-object limit of '{this._owner.DisplayName}' — place large files on a local member");

      this._Materialise(); // a partial write composes over the existing bytes
      base.Write(buffer, offset, count);
      this._dirty = true;
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      if (this.Position + buffer.Length > MaxFileSize)
        throw new PoolFsException(PoolFsError.NoSpace, $"File exceeds the {MaxFileSize} byte whole-object limit of '{this._owner.DisplayName}' — place large files on a local member");

      this._Materialise();
      base.Write(buffer);
      this._dirty = true;
    }

    public override void WriteByte(byte value) {
      if (this.Position + 1 > MaxFileSize)
        throw new PoolFsException(PoolFsError.NoSpace, $"File exceeds the {MaxFileSize} byte whole-object limit of '{this._owner.DisplayName}'");

      this._Materialise();
      base.WriteByte(value);
      this._dirty = true;
    }

    public override void CopyTo(Stream destination, int bufferSize) {
      this._Materialise();
      base.CopyTo(destination, bufferSize);
    }

    public override void SetLength(long value) {
      if (value > MaxFileSize)
        throw new PoolFsException(PoolFsError.NoSpace, $"File exceeds the {MaxFileSize} byte whole-object limit of '{this._owner.DisplayName}'");

      // truncating to zero discards the whole object unread — the one case where the download
      // is provably pointless, and exactly what every publish does first
      if (value == 0)
        Interlocked.Exchange(ref this._loadInitial, null);
      else
        this._Materialise();

      base.SetLength(value);
      this._dirty = true;
    }

    public override void Flush() {
      if (!this._dirty)
        return;

      // stream out of the staging buffer rather than ToArray(): copying it would briefly hold the
      // object TWICE, and a provider that streams uploads never needs the array at all
      this.Position = 0;
      this._owner._UploadStream(this._physical, this, this.Length);
      this._dirty = false;
    }

    protected override void Dispose(bool disposing) {
      if (disposing && this._dirty)
        this.Flush();

      base.Dispose(disposing);
    }
  }

  public void Truncate(string relativePath, bool shadow, long length) => this._Guard(() => {
    if (length is < 0 or > MaxFileSize)
      throw new PoolFsException(PoolFsError.NoSpace, $"Length {length} exceeds the {MaxFileSize} byte whole-object limit of '{this.DisplayName}'");

    var physical = _File(relativePath, shadow);

    // truncating to nothing keeps nothing, so downloading the object first is pure waste — and
    // this is the COMMON case: every whole-file publish opens the target and does SetLength(0)
    // before streaming, so a heal or drain onto a remote used to pull the entire old object over
    // the wire, into RAM, purely to discard it
    if (length == 0) {
      this._store.Upload(physical, []);
      this._InvalidateReadCache(physical);
      return;
    }

    // Shrink reads the surviving prefix and writes it back to the SAME object, so the two cannot
    // overlap — on a local-filesystem store that is a sharing violation outright, and on a remote
    // one it is a read racing its own overwrite. The prefix is spooled first: with ranges only
    // the kept bytes cross the wire, and the spool keeps RAM bounded regardless of object size,
    // where the previous version held the WHOLE pre-truncation object in memory.
    Stream spooled;
    using (var prefix = this._store.OpenReadRange(physical, 0, length))
      spooled = _Spool(prefix, length); // the read is CLOSED before the write opens

    using (spooled)
      this._store.Upload(physical, spooled, length);

    this._InvalidateReadCache(physical);
  });

  /// <summary>Above this, a spool goes to disk rather than to memory.</summary>
  private const int _SPOOL_TO_DISK_ABOVE = 8 * 1024 * 1024;

  /// <summary>
  /// Materialises a stream so it can be handed to a writer targeting the same object. Small
  /// payloads stay in memory; larger ones spool to a temp file that deletes itself on close, so
  /// the cost is bounded by disk rather than by RAM.
  /// </summary>
  private static Stream _Spool(Stream source, long length) {
    if (length <= _SPOOL_TO_DISK_ABOVE) {
      var buffer = new MemoryStream((int)Math.Max(0, length));
      source.CopyTo(buffer);
      buffer.Position = 0;
      return buffer;
    }

    var temp = new FileStream(Path.Combine(Path.GetTempPath(), $"dbspool-{Guid.NewGuid():N}.tmp"),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
    source.CopyTo(temp);
    temp.Position = 0;
    return temp;
  }

  public void Delete(string relativePath, bool shadow) => this._Guard(() => {
    var physical = _File(relativePath, shadow);
    if (this._store.Stat(physical) is not { IsFolder: false })
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

    this._store.DeleteFile(physical);
    this._InvalidateReadCache(physical);
  });

  public void EnsureFolder(string relativeFolder, bool shadow)
    => this._Guard(() => this._EnsureFolderRecursive(_Folder(relativeFolder, shadow)));

  private void _EnsureFolderRecursive(string physicalFolder) {
    if (physicalFolder.Length == 0 || this._store.Stat(physicalFolder) is { IsFolder: true })
      return;

    this._EnsureFolderRecursive(PoolPaths.GetParent(physicalFolder));
    this._store.CreateFolder(physicalFolder);
  }

  public void DeleteFolder(string relativeFolder, bool shadow) => this._Guard(() => {
    var physical = _Folder(relativeFolder, shadow);
    if (this._store.Stat(physical) is not { IsFolder: true })
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {relativeFolder}");

    if (this._store.List(physical).Any())
      throw new PoolFsException(PoolFsError.NotEmpty, $"Folder not empty: {relativeFolder}");

    this._store.DeleteFolder(physical);
  });

  /// <summary>No trusted atomic rename on these backends: the engine publishes via <c>WholeFilePublisher</c>'s put-and-verify path instead (FR-CAP-ADAPT).</summary>
  public void AtomicReplace(string tempRelative, string finalRelative, bool shadow)
    => throw new PoolFsException(PoolFsError.NotSupported, $"Backend '{this.DisplayName}' has no atomic rename — use whole-file publication");

  /// <summary>
  /// Whole-file backends have no server-side directory move: the subtree is copied object by
  /// object and the source removed afterwards (FR-CAP-ADAPT — correctness over speed).
  /// </summary>
  public void RenameFolder(string fromRelativeFolder, string toRelativeFolder) => this._Guard(() => {
    var fromPhysical = _Folder(fromRelativeFolder, false);
    var toPhysical = _Folder(toRelativeFolder, false);
    if (this._store.Stat(fromPhysical) is not { IsFolder: true })
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {fromRelativeFolder}");
    if (this._store.Stat(toPhysical) != null)
      throw new PoolFsException(PoolFsError.Exists, $"Target already exists: {toRelativeFolder}");

    this._EnsureFolderRecursive(toPhysical);
    this._MoveTree(fromPhysical, toPhysical);
    this._store.DeleteFolder(fromPhysical);
    this._ClearReadCache(); // a whole subtree moved — drop any cached objects under the old prefix
  });

  private void _MoveTree(string fromPhysical, string toPhysical) {
    foreach (var entry in this._store.List(fromPhysical).ToArray()) {
      var source = $"{fromPhysical}/{entry.Name}";
      var target = $"{toPhysical}/{entry.Name}";
      if (entry.IsFolder) {
        this._store.CreateFolder(target);
        this._MoveTree(source, target);
        this._store.DeleteFolder(source);
      } else {
        // streamed, not buffered: `Upload(target, Download(source))` held the entire object in
        // memory for every file in the subtree — up to the 2 GiB whole-object cap each — for a
        // move that never needs to see the bytes at all
        using (var content = this._store.OpenRead(source))
          this._store.Upload(target, content, entry.Length);

        this._store.DeleteFile(source);
      }
    }
  }

  public FileMeta? Stat(string relativePath, bool shadow) => this._Guard<FileMeta?>(() => {
    var meta = this._store.Stat(_File(relativePath, shadow));
    return meta == null
      ? null
      : new FileMeta(meta.Length, meta.CreatedUtc, meta.ModifiedUtc, meta.IsFolder ? FileAttributes.Directory : FileAttributes.Normal);
  });

  public bool FileExists(string relativePath, bool shadow)
    => this._TryQuery(() => this._store.Stat(_File(relativePath, shadow)) is { IsFolder: false });

  public bool FolderExists(string relativeFolder, bool shadow) {
    var physical = _Folder(relativeFolder, shadow);
    return physical.Length == 0 ? this.IsOnline : this._TryQuery(() => this._store.Stat(physical) is { IsFolder: true });
  }

  /// <summary>
  /// Answers an existence question, distinguishing "the store said no" from "the store did not
  /// answer". A definitive absence comes back as a null <c>Stat</c> — NOT as an exception — so
  /// every exception here is a transport or protocol failure. Reporting that as <c>false</c> was
  /// actively dangerous: <c>Unlink</c> skips a member whose copy "does not exist", leaving a
  /// ghost that resurrects on the next mount, and placement concludes a copy is missing and
  /// heals over a member that in fact holds the newest data.
  /// </summary>
  private bool _TryQuery(Func<bool> query) {
    try {
      this._EnsureConnected();
      return query();
    } catch (Exception) {
      this._MarkUnreachable();
      return false;
    }
  }

  public IEnumerable<VolumeEntry> List(string relativeFolder, bool shadow) {
    var physical = _Folder(relativeFolder, shadow);
    if (physical.Length > 0 && this._Guard(() => this._store.Stat(physical)) is not { IsFolder: true })
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {relativeFolder}");

    foreach (var entry in this._Guard(() => this._store.List(physical).ToArray()))
      yield return new(entry.Name, entry.IsFolder, entry.Length, entry.ModifiedUtc);
  }

  /// <summary>Remote services own their timestamps; per FR-ATTR this reports NotSupported instead of lying.</summary>
  public void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc)
    => throw new PoolFsException(PoolFsError.NotSupported, $"Backend '{this.DisplayName}' cannot set timestamps");

  public void Dispose() => this._store.Dispose();

}

/// <summary>
/// <summary>
/// Serializes every call to a non-thread-safe store under one lock, so the engine's concurrent
/// prefetch / mirror-split reads (and any background op) never interleave protocol commands on a
/// single-connection backend (FTP/SFTP). Correctness over throughput — but the object read cache
/// already collapses a file's per-block reads to one download, so the real contention is low.
/// </summary>
internal sealed class SerializingWholeFileStore(IWholeFileStore inner) : IWholeFileStore {
  private readonly Lock _lock = new();

  public bool ThreadSafe => true; // it IS now — this wrapper provides the safety

  public void Connect() { lock (this._lock) inner.Connect(); }
  public bool Probe() { lock (this._lock) return inner.Probe(); }
  public byte[] Download(string p) { lock (this._lock) return inner.Download(p); }
  public void Upload(string p, byte[] c) { lock (this._lock) inner.Upload(p, c); }
  public void DeleteFile(string p) { lock (this._lock) inner.DeleteFile(p); }
  public StoreMeta? Stat(string p) { lock (this._lock) return inner.Stat(p); }
  public void CreateFolder(string p) { lock (this._lock) inner.CreateFolder(p); }
  public void DeleteFolder(string p) { lock (this._lock) inner.DeleteFolder(p); }

  // materialise inside the lock — the enumerable must not be lazily walked after the lock releases
  public IEnumerable<StoreEntry> List(string p) { lock (this._lock) return inner.List(p).ToArray(); }

  public void Dispose() { lock (this._lock) inner.Dispose(); }
}

/// <summary>
/// <see cref="IWholeFileStore"/> over a plain local directory — the headless test double
/// every remote store shares its code path with, and a usable endpoint in its own right.
/// </summary>
public sealed class DirectoryStore(string rootPath) : IWholeFileStore {

  private string _Map(string physicalPath) => Path.Combine(rootPath, physicalPath.Replace('/', Path.DirectorySeparatorChar));

  public bool ThreadSafe => true; // independent File/Directory ops — safe under concurrent use

  /// <summary>A real filesystem does all of this natively — seek for a range, stream for transfer.</summary>
  public StoreCaps Caps => StoreCaps.RangeRead | StoreCaps.StreamingUpload | StoreCaps.StreamingDownload;

  public void Connect() {
  }

  public bool Probe() => Directory.Exists(rootPath);

  public byte[] Download(string physicalPath) => File.ReadAllBytes(this._Map(physicalPath));

  public Stream OpenRead(string physicalPath) => File.OpenRead(this._Map(physicalPath));

  public Stream OpenReadRange(string physicalPath, long offset, long count) {
    if (count <= 0)
      return new MemoryStream([], false);

    var stream = File.OpenRead(this._Map(physicalPath));
    if (offset >= stream.Length) {
      stream.Dispose();
      return new MemoryStream([], false);
    }

    stream.Seek(offset, SeekOrigin.Begin);
    return new BoundedFileStream(stream, Math.Min(count, stream.Length - offset));
  }

  /// <summary>Stops a seeked file stream at the end of the requested window.</summary>
  private sealed class BoundedFileStream(Stream inner, long count) : Stream {

    private long _delivered;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => count;

    public override long Position {
      get => this._delivered;
      set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int readCount) {
      var allowed = (int)Math.Min(readCount, count - this._delivered);
      if (allowed <= 0)
        return 0;

      var read = inner.Read(buffer, offset, allowed);
      this._delivered += read;
      return read;
    }

    public override void Flush() {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int writeCount) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
      if (disposing)
        inner.Dispose();

      base.Dispose(disposing);
    }
  }

  public void Upload(string physicalPath, byte[] content) {
    var target = this._Map(physicalPath);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllBytes(target, content);
  }

  public void Upload(string physicalPath, Stream content, long length = -1) {
    var target = this._Map(physicalPath);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    using var file = File.Create(target);
    content.CopyTo(file);
  }

  public void DeleteFile(string physicalPath) => File.Delete(this._Map(physicalPath));

  public StoreMeta? Stat(string physicalPath) {
    var target = this._Map(physicalPath);
    var file = new FileInfo(target);
    if (file.Exists)
      return new(false, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc);

    var directory = new DirectoryInfo(target);
    return directory.Exists ? new(true, 0, directory.CreationTimeUtc, directory.LastWriteTimeUtc) : null;
  }

  public void CreateFolder(string physicalPath) => Directory.CreateDirectory(this._Map(physicalPath));

  public void DeleteFolder(string physicalPath) => Directory.Delete(this._Map(physicalPath), false);

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    var directory = new DirectoryInfo(physicalFolder.Length == 0 ? rootPath : this._Map(physicalFolder));
    foreach (var item in directory.EnumerateFileSystemInfos())
      yield return item switch {
        FileInfo file => new(file.Name, false, file.Length, file.LastWriteTimeUtc),
        _ => new StoreEntry(item.Name, true, 0, item.LastWriteTimeUtc),
      };
  }

  public void Dispose() {
  }

}
