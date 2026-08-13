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

  /// <summary>
  /// Handles the APPLICATION still has open, which is what the drain/heal guards must look at.
  ///
  /// On Windows this is deliberately not <see cref="HandleCount"/>. WinFsp sends CLEANUP when the
  /// application closes its last handle and CLOSE when the kernel finally releases the file
  /// object, and the FSD defers CLOSE for a long time — often until unmount. Counting kernel file
  /// objects therefore leaves an ordinary written file looking permanently "in use", so the
  /// drainer and the healer skip it forever: a file written by a long-running application never
  /// gets its owed second copy, which is a durability loss (SAFE-DUP) and not merely a delay.
  /// </summary>
  internal int AppHandleCount;

  /// <summary>Per-handle read-ahead detectors keyed by handle value.</summary>
  internal readonly Dictionary<long, ReadAheadState> ReadAhead = [];
}

/// <summary>
/// Central handle table (§6.4): maps open handles to their shared per-file state,
/// refcounted so the state object lives exactly as long as any handle on the path.
/// </summary>
public sealed class HandleTable {

  public sealed record OpenHandle(NodeHandle Handle, FileState File, AccessMode Access) {
    /// <summary>Set once the application has closed this handle, so it is decounted exactly once.</summary>
    internal bool ApplicationClosed;
  }

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

  /// <summary>
  /// Locks the state that is CANONICAL for the path — the one <see cref="_files"/> maps it to at
  /// the moment the lock is held, re-checked after acquiring.
  ///
  /// The re-check is load-bearing. A replacing rename repoints a path at a different state, so a
  /// caller that resolved the state, then waited for its lock, can wake up holding a state the
  /// path no longer refers to. Two threads would then be "locking the same path" through two
  /// different locks and excluding nothing — which shows up as a torn read, one block from each
  /// writer. Losing the race just means going round again against the new state.
  /// </summary>
  private PathLease? _Acquire(string normalizedPath, bool write, TimeSpan timeout) {
    var deadline = timeout == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + timeout;
    while (true) {
      FileState file;
      lock (this._lock) {
        if (!this._files.TryGetValue(normalizedPath, out var existing))
          this._files.Add(normalizedPath, existing = new(normalizedPath));

        file = existing;
        ++file.RefCount; // pin the state so it survives until the lease is released
      }

      // NEVER block on the file lock while holding the table lock — Open/Close would stall.
      // A zero timeout is a legitimate "try once, do not wait" (the drainer and heal use it to
      // skip a busy file rather than stall the pump), so it still makes the attempt.
      var remaining = timeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : deadline - DateTime.UtcNow;
      if (remaining != Timeout.InfiniteTimeSpan && remaining < TimeSpan.Zero)
        remaining = TimeSpan.Zero;

      bool taken;
      try {
        taken = write ? file.Lock.TryEnterWriteLock(remaining) : file.Lock.TryEnterReadLock(remaining);
      } catch {
        this._ReleaseState(file);
        throw;
      }

      if (!taken) {
        this._ReleaseState(file);
        return null;
      }

      lock (this._lock)
        if (this._files.TryGetValue(normalizedPath, out var current) && ReferenceEquals(current, file))
          return new(this, file, write); // still the path's state — the lease is meaningful

      // superseded while we waited: release and resolve again
      if (write)
        file.Lock.ExitWriteLock();
      else
        file.Lock.ExitReadLock();

      this._ReleaseState(file);
      if (deadline != DateTime.MaxValue && DateTime.UtcNow >= deadline)
        return null;
    }
  }

  /// <summary>
  /// Drops a pin, unkeying the state only when it is still the one registered for its path. A
  /// replacing rename can leave a state whose Path names an entry that now belongs to a DIFFERENT
  /// state; removing that blindly evicted the live entry and let a second state be created for the
  /// same path — the mutual exclusion is only as good as the one-state-per-path invariant.
  /// </summary>
  internal void _ReleaseState(FileState file) {
    lock (this._lock)
      if (--file.RefCount == 0 && this._files.TryGetValue(file.Path, out var current) && ReferenceEquals(current, file))
        this._files.Remove(file.Path);
  }

  public OpenHandle Open(string normalizedPath, AccessMode access) {
    lock (this._lock) {
      if (!this._files.TryGetValue(normalizedPath, out var file))
        this._files.Add(normalizedPath, file = new(normalizedPath));

      ++file.RefCount;
      ++file.HandleCount;
      ++file.AppHandleCount;
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
      if (!open.ApplicationClosed) {
        open.ApplicationClosed = true; // a handle closed without an explicit cleanup still counts
        --open.File.AppHandleCount;
      }

      if (--open.File.RefCount == 0 && this._files.TryGetValue(open.File.Path, out var current) && ReferenceEquals(current, open.File))
        this._files.Remove(open.File.Path); // only ever unkey OUR OWN entry (see _ReleaseState)
    }
  }

  /// <summary>
  /// True when any HANDLE is open on the path (used by the drain/heal guards). A path lease
  /// pins the same state but is deliberately NOT counted — a background job holding its own
  /// lease must not read that back as "a foreground handle is open".
  /// </summary>
  public bool IsOpen(string normalizedPath) {
    lock (this._lock)
      return this._files.TryGetValue(normalizedPath, out var file) && file.AppHandleCount > 0;
  }

  /// <summary>The path a handle refers to, or null when it is already gone.</summary>
  public string? TryGetPath(NodeHandle handle) {
    lock (this._lock)
      return this._handles.TryGetValue(handle.Value, out var open) ? open.File.Path : null;
  }

  /// <summary>
  /// Records that the APPLICATION has closed this handle, even though the handle itself stays
  /// valid for whatever the kernel still sends against it (cached writes, a deferred close).
  ///
  /// Idempotent: the adapter may send it more than once, and the eventual close must not decount
  /// the same handle twice.
  /// </summary>
  public void MarkApplicationClosed(NodeHandle handle) {
    lock (this._lock) {
      if (!this._handles.TryGetValue(handle.Value, out var open) || open.ApplicationClosed)
        return;

      open.ApplicationClosed = true;
      --open.File.AppHandleCount;
    }
  }

  /// <summary>
  /// Follows an open file across a rename so existing handles stay valid.
  ///
  /// CONTRACT: the caller must hold an EXCLUSIVE lease on BOTH endpoints for the whole rename
  /// (see <see cref="PoolFileSystem.Rename"/>, which orders them so opposing renames cannot
  /// deadlock). This repoints a path at a different state, and a replacing rename displaces the
  /// state that was there; without the leases, a thread could be mid-operation on either side and
  /// the one-state-per-path invariant — the thing every per-file lock rests on — would not hold.
  /// </summary>
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
