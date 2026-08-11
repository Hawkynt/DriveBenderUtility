namespace DivisonM.Vfs.Engine;

/// <summary>
/// Per-file state shared by all handles open on the same path (FR-CONCURRENCY): a
/// single owner for that file's lock and (from M2 on) its dirty write buffer, so no two
/// writers ever race. Locks are per-file, never global.
/// </summary>
public sealed class FileState(string normalizedPath) {
  public string Path { get; internal set; } = normalizedPath;
  public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.NoRecursion);

  /// <summary>Total pins keeping this state alive: open handles PLUS outstanding path leases.</summary>
  internal int RefCount;

  /// <summary>Open handles only — a lease must not make a path look "open" to the drain/heal guards.</summary>
  internal int HandleCount;

  /// <summary>Per-handle read-ahead detectors keyed by handle value.</summary>
  internal readonly Dictionary<long, ReadAheadState> ReadAhead = [];
}

/// <summary>
/// Central handle table (§6.4): maps open handles to their shared per-file state,
/// refcounted so the state object lives exactly as long as any handle on the path.
/// </summary>
public sealed class HandleTable {

  public sealed record OpenHandle(NodeHandle Handle, FileState File, AccessMode Access);

  /// <summary>
  /// A scoped lock on one path's <see cref="FileState"/> that does NOT require an open
  /// handle (SAFE-NOLOSS). Background mutators — owed-copy sync, the landing-zone drainer,
  /// heal, staged publication — and the structural foreground ops take this, so they
  /// serialise against <c>Read</c>/<c>Write</c>/<c>SetLength</c>, which lock the very same
  /// object through their handle. Without it those jobs mutated copies under a foreground
  /// writer's feet and only an <c>IsDirty</c>/<c>IsOpen</c> TOCTOU check stood in the way.
  /// </summary>
  public sealed class PathLease : IDisposable {

    private readonly HandleTable _owner;
    private readonly bool _write;
    private FileState? _file;

    internal PathLease(HandleTable owner, FileState file, bool write) {
      this._owner = owner;
      this._file = file;
      this._write = write;
    }

    /// <summary>The locked state — its <c>Path</c> follows renames taken under this lease.</summary>
    public FileState File => this._file ?? throw new ObjectDisposedException(nameof(PathLease));

    public void Dispose() {
      var file = Interlocked.Exchange(ref this._file, null);
      if (file == null)
        return; // idempotent — a lease may be disposed twice by nested using blocks

      if (this._write)
        file.Lock.ExitWriteLock();
      else
        file.Lock.ExitReadLock();

      this._owner._ReleaseState(file);
    }
  }

  private readonly Dictionary<long, OpenHandle> _handles = [];
  private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
  private readonly Lock _lock = new();
  private long _nextHandle;

  /// <summary>Exclusive lease on a path; blocks until granted.</summary>
  public PathLease AcquireWrite(string normalizedPath)
    => this._Acquire(normalizedPath, write: true, Timeout.InfiniteTimeSpan)!;

  /// <summary>Shared lease on a path; blocks until granted.</summary>
  public PathLease AcquireRead(string normalizedPath)
    => this._Acquire(normalizedPath, write: false, Timeout.InfiniteTimeSpan)!;

  /// <summary>
  /// Exclusive lease, or null when it could not be granted in time. Used where a caller
  /// already holds another path's lease (the write-buffer throttle) and must therefore
  /// never block indefinitely — two writers throttling against each other would deadlock.
  /// </summary>
  public PathLease? TryAcquireWrite(string normalizedPath, TimeSpan timeout)
    => this._Acquire(normalizedPath, write: true, timeout);

  private PathLease? _Acquire(string normalizedPath, bool write, TimeSpan timeout) {
    FileState file;
    lock (this._lock) {
      if (!this._files.TryGetValue(normalizedPath, out var existing))
        this._files.Add(normalizedPath, existing = new(normalizedPath));

      file = existing;
      ++file.RefCount; // pin the state so it survives until the lease is released
    }

    // NEVER block on the file lock while holding the table lock — Open/Close would stall
    bool taken;
    try {
      taken = write ? file.Lock.TryEnterWriteLock(timeout) : file.Lock.TryEnterReadLock(timeout);
    } catch {
      this._ReleaseState(file);
      throw;
    }

    if (taken)
      return new(this, file, write);

    this._ReleaseState(file);
    return null;
  }

  internal void _ReleaseState(FileState file) {
    lock (this._lock)
      if (--file.RefCount == 0)
        this._files.Remove(file.Path); // Path follows renames, so this always unkeys the right entry
  }

  public OpenHandle Open(string normalizedPath, AccessMode access) {
    lock (this._lock) {
      if (!this._files.TryGetValue(normalizedPath, out var file))
        this._files.Add(normalizedPath, file = new(normalizedPath));

      ++file.RefCount;
      ++file.HandleCount;
      var handle = new NodeHandle(++this._nextHandle);
      var open = new OpenHandle(handle, file, access);
      this._handles.Add(handle.Value, open);
      return open;
    }
  }

  public OpenHandle Get(NodeHandle handle) {
    lock (this._lock)
      return this._handles.TryGetValue(handle.Value, out var open)
        ? open
        : throw new PoolFsException(PoolFsError.StaleHandle, $"Handle {handle.Value} is not open");
  }

  public void Close(NodeHandle handle) {
    lock (this._lock) {
      if (!this._handles.Remove(handle.Value, out var open))
        throw new PoolFsException(PoolFsError.StaleHandle, $"Handle {handle.Value} is not open");

      // the read path mutates ReadAhead under lock(File.ReadAhead) — take the SAME lock here so
      // a concurrent Read + Close on one file never corrupts the dictionary
      lock (open.File.ReadAhead)
        open.File.ReadAhead.Remove(handle.Value);
      --open.File.HandleCount;
      if (--open.File.RefCount == 0)
        this._files.Remove(open.File.Path);
    }
  }

  /// <summary>
  /// True when any HANDLE is open on the path (used by the drain/heal guards). A path lease
  /// pins the same state but is deliberately NOT counted — a background job holding its own
  /// lease must not read that back as "a foreground handle is open".
  /// </summary>
  public bool IsOpen(string normalizedPath) {
    lock (this._lock)
      return this._files.TryGetValue(normalizedPath, out var file) && file.HandleCount > 0;
  }

  /// <summary>Follows an open file across a rename so existing handles stay valid.</summary>
  public void RenamePath(string fromNormalized, string toNormalized) {
    lock (this._lock) {
      if (!this._files.Remove(fromNormalized, out var file))
        return;

      file.Path = toNormalized;
      this._files[toNormalized] = file;
    }
  }

  /// <summary>Follows every open file under a renamed folder so their handles stay valid (folder FR-RENAME).</summary>
  public void RenameSubtree(string fromNormalized, string toNormalized) {
    lock (this._lock) {
      var fromPrefix = fromNormalized + "/";
      foreach (var key in this._files.Keys.Where(k => k.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray()) {
        var file = this._files[key];
        this._files.Remove(key);
        file.Path = toNormalized + "/" + key[fromPrefix.Length..];
        this._files[file.Path] = file;
      }
    }
  }

  public int OpenHandleCount {
    get {
      lock (this._lock)
        return this._handles.Count;
    }
  }

}
