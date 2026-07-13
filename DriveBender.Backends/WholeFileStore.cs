using DivisonM.Vfs;
using Hawkynt.CloudStorage;

namespace DivisonM.Backends;

public readonly record struct StoreEntry(string Name, bool IsFolder, long Length, DateTime ModifiedUtc);

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
/// member (§6.1): reads buffer the object so the engine can seek, writes stage in memory
/// and upload on flush (read-modify-write for positional writes). The capability set
/// carries neither <see cref="BackendCaps.AtomicRename"/> nor
/// <see cref="BackendCaps.DurableFlush"/>, so the engine journals around the gaps and
/// never counts such a member toward the ack quorum (FR-CAP-ADAPT, SAFE-REMOTE).
/// </summary>
public sealed class WholeFileVolumeIO(Guid memberId, string displayName, string physicalVolumeId, IWholeFileStore store, Func<DateTime>? clock = null) : IVolumeIO, IDisposable {

  private static readonly TimeSpan _PROBE_TTL = TimeSpan.FromSeconds(30);

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
  private DateTime _lastProbeUtc = DateTime.MinValue;
  private bool _lastProbeResult;
  private bool _connected;

  // The engine reads a file BLOCK BY BLOCK, and each block miss otherwise re-downloads the WHOLE
  // object (a cold 1 GiB cloud read in 128 KiB blocks would move ~8000× the file). A bounded LRU of
  // recently-downloaded objects collapses that burst back to ONE download per file (O(n²)→O(n)).
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

  public bool IsOnline {
    get {
      var now = this._clock();
      if (now - this._lastProbeUtc < _PROBE_TTL)
        return this._lastProbeResult;

      this._lastProbeUtc = now;
      try {
        this._EnsureConnected();
        this._lastProbeResult = this._store.Probe();
      } catch (Exception) {
        this._lastProbeResult = false;
      }

      return this._lastProbeResult;
    }
  }

  private void _EnsureConnected() {
    if (this._connected)
      return;

    this._store.Connect();
    this._connected = true;
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
    // whole-file staging (FR-REMOTE-READ): buffer the object so the engine can seek. The bounded
    // object cache serves the engine's per-block reads of one file from a SINGLE download; the
    // read-only MemoryStream shares the cached array (never mutated) so concurrent readers are safe.
    var physical = _File(relativePath, shadow);
    if (this._store.Stat(physical) is not { IsFolder: false })
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

    return new MemoryStream(this._CachedDownload(physical), writable: false);
  });

  public Stream OpenWrite(string relativePath, bool shadow, bool create) => this._Guard<Stream>(() => {
    var physical = _File(relativePath, shadow);
    var exists = this._store.Stat(physical) is { IsFolder: false };
    if (!exists && !create)
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

    if (!exists)
      this._EnsureFolderRecursive(PoolPaths.GetParent(physical));

    // read-modify-write staging: existing content preloads so positional writes compose;
    // a brand-new file starts dirty so even an empty create uploads on flush (the cache copy is
    // safe: UploadOnFlushStream copies it into its own buffer before mutating)
    var initial = exists ? this._CachedDownload(physical) : [];
    return new UploadOnFlushStream(this, physical, initial, startDirty: !exists);
  });

  private void _Upload(string physical, byte[] content) => this._Guard(() => {
    this._store.Upload(physical, content);
    this._InvalidateReadCache(physical); // the object changed — drop any stale cached copy
  });

  /// <summary>Staging stream: mutations happen in memory; Flush uploads the whole object (whole-file model, §6.1).</summary>
  private sealed class UploadOnFlushStream : MemoryStream {

    private readonly WholeFileVolumeIO _owner;
    private readonly string _physical;
    private bool _dirty;

    public UploadOnFlushStream(WholeFileVolumeIO owner, string physical, byte[] initial, bool startDirty) {
      this._owner = owner;
      this._physical = physical;
      this._dirty = startDirty;
      if (initial.Length > 0) {
        base.Write(initial, 0, initial.Length);
        this.Position = 0;
      }
    }

    public override void Write(byte[] buffer, int offset, int count) {
      if (this.Position + count > MaxFileSize)
        throw new PoolFsException(PoolFsError.NoSpace, $"File exceeds the {MaxFileSize} byte whole-object limit of '{this._owner.DisplayName}' — place large files on a local member");

      base.Write(buffer, offset, count);
      this._dirty = true;
    }

    public override void WriteByte(byte value) {
      if (this.Position + 1 > MaxFileSize)
        throw new PoolFsException(PoolFsError.NoSpace, $"File exceeds the {MaxFileSize} byte whole-object limit of '{this._owner.DisplayName}'");

      base.WriteByte(value);
      this._dirty = true;
    }

    public override void SetLength(long value) {
      if (value > MaxFileSize)
        throw new PoolFsException(PoolFsError.NoSpace, $"File exceeds the {MaxFileSize} byte whole-object limit of '{this._owner.DisplayName}'");

      base.SetLength(value);
      this._dirty = true;
    }

    public override void Flush() {
      if (!this._dirty)
        return;

      this._owner._Upload(this._physical, this.ToArray());
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
    var content = this._store.Download(physical);
    Array.Resize(ref content, (int)length); // guarded above — no silent wrap
    this._store.Upload(physical, content);
    this._InvalidateReadCache(physical);
  });

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
        this._store.Upload(target, this._store.Download(source));
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

  private bool _TryQuery(Func<bool> query) {
    try {
      this._EnsureConnected();
      return query();
    } catch (Exception) {
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

  public void Connect() {
  }

  public bool Probe() => Directory.Exists(rootPath);

  public byte[] Download(string physicalPath) => File.ReadAllBytes(this._Map(physicalPath));

  public void Upload(string physicalPath, byte[] content) {
    var target = this._Map(physicalPath);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllBytes(target, content);
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
