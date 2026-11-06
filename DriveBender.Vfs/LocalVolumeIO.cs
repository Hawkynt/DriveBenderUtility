using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DivisonM.Vfs;

/// <summary>
/// <see cref="IVolumeIO"/> over a local (or UNC-mapped) directory tree — the backend for
/// drive-root, subfolder and UNC members. Honours the Drive Bender on-disk layout via
/// <see cref="PoolPaths"/> (SAFE-COMPAT). I/O goes through a pooled-handle layer with
/// positional (offset-based) reads/writes: no per-chunk file opens, and any number of
/// threads can hit the same file concurrently without seek-state races (NFR-THROUGHPUT).
/// </summary>
public sealed class LocalVolumeIO(Guid memberId, string displayName, string rootPath, string physicalVolumeId) : IVolumeIO {

  #region pooled positional I/O

  /// <summary>A refcounted OS handle: streams borrow it; it closes when evicted AND unused.</summary>
  private sealed class HandleLease(SafeFileHandle handle) {
    public readonly SafeFileHandle Handle = handle;
    private int _refs = 1; // the pool's own reference
    public bool Evicted;
    public long LastUseTicks = Environment.TickCount64;

    public void AddRef() {
      lock (this) {
        ++this._refs;
        this.LastUseTicks = Environment.TickCount64;
      }
    }

    public void Release() {
      bool close;
      lock (this)
        close = --this._refs == 0 && this.Evicted;
      if (close)
        this.Handle.Dispose();
    }
  }

  /// <summary>
  /// LRU pool of open file handles keyed by (access, physical path). Writable handles carry
  /// WRITE_THROUGH so every positional write is a durability barrier (SAFE-FSYNC parity with
  /// the previous per-call streams). Idle handles retire after a short TTL so directory
  /// metadata (sizes) stays fresh for outside observers.
  /// </summary>
  private sealed class HandlePool {
    private const int _CAPACITY = 64;
    private const long _IDLE_TTL_MS = 3000;

    // Keyed the way the HOST filesystem resolves names. Case-insensitively on Linux, the pooled
    // handle opened for 'Report.txt' would be handed back for a read of 'REPORT.TXT' — two genuinely
    // different files on ext4 — so one file's bytes would serve the other's reads for as long as the
    // handle stayed pooled.
    private readonly Dictionary<string, HandleLease> _leases = new(PoolPaths.PathComparer);
    private readonly Lock _lock = new();

    private static string _Key(string physicalPath, bool writable) => (writable ? "w|" : "r|") + physicalPath;

    public HandleLease Rent(string physicalPath, bool writable, bool create) {
      var key = _Key(physicalPath, writable);
      lock (this._lock) {
        this._EvictIdle();
        if (this._leases.TryGetValue(key, out var cached)) {
          cached.AddRef();
          return cached;
        }
      }

      // the actual open happens outside the pool lock — it is disk I/O
      var handle = File.OpenHandle(
        physicalPath,
        writable ? (create ? FileMode.OpenOrCreate : FileMode.Open) : FileMode.Open,
        writable ? FileAccess.ReadWrite : FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        writable ? FileOptions.WriteThrough : FileOptions.None);
      var fresh = new HandleLease(handle);
      fresh.AddRef(); // the caller's reference

      lock (this._lock) {
        if (this._leases.TryGetValue(key, out var raced)) {
          // lost an open race: keep the established lease, retire ours
          fresh.Evicted = true;
          fresh.Release(); // caller ref
          fresh.Release(); // pool ref → closes
          raced.AddRef();
          return raced;
        }

        this._leases[key] = fresh;
        if (this._leases.Count > _CAPACITY)
          this._EvictOldest();
        return fresh;
      }
    }

    /// <summary>The authoritative length while a pooled write handle is open (directory metadata lags on NTFS).</summary>
    public bool TryGetWriteLength(string physicalPath, out long length) {
      lock (this._lock) {
        if (this._leases.TryGetValue(_Key(physicalPath, true), out var lease)) {
          length = RandomAccess.GetLength(lease.Handle);
          return true;
        }
      }

      length = 0;
      return false;
    }

    public void Invalidate(string physicalPath) {
      lock (this._lock) {
        this._Retire(_Key(physicalPath, false));
        this._Retire(_Key(physicalPath, true));
      }
    }

    public void InvalidatePrefix(string physicalPrefix) {
      lock (this._lock)
        foreach (var key in this._leases.Keys.Where(k => k.AsSpan(2).StartsWith(physicalPrefix, PoolPaths.PathComparison)).ToArray())
          this._Retire(key);
    }

    private void _Retire(string key) {
      if (!this._leases.Remove(key, out var lease))
        return;

      lease.Evicted = true;
      lease.Release(); // pool ref — closes once the last borrower returns it
    }

    /// <summary>Drops every handle idle past the TTL. Safe to call at any time; borrowed handles are untouched.</summary>
    public void EvictIdleNow() {
      lock (this._lock)
        this._EvictIdle();
    }

    private void _EvictIdle() {
      var now = Environment.TickCount64;
      foreach (var (key, lease) in this._leases.Where(kv => now - kv.Value.LastUseTicks > _IDLE_TTL_MS).ToArray())
        this._Retire(key);
    }

    private void _EvictOldest() {
      var oldest = this._leases.OrderBy(kv => kv.Value.LastUseTicks).First();
      this._Retire(oldest.Key);
    }
  }

  /// <summary>Positional stream over a leased handle: offset-based I/O, no shared seek state.</summary>
  private sealed class PooledStream(HandleLease lease, bool writable) : Stream {
    private long _position;
    private int _disposed;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => writable;
    public override long Length => RandomAccess.GetLength(lease.Handle);
    public override long Position { get => this._position; set => this._position = value; }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

    /// <summary>
    /// The span overloads are the REAL implementations, and overriding them matters.
    ///
    /// <see cref="Stream"/>'s base versions satisfy a span caller by renting an array, copying
    /// into it and copying back out — so every <see cref="Stream.CopyTo(Stream)"/>, which uses the
    /// span path internally, paid for two copies it did not need. Going straight to
    /// <see cref="RandomAccess"/> is one syscall against the caller's own memory.
    /// </summary>
    public override int Read(Span<byte> buffer) {
      var read = RandomAccess.Read(lease.Handle, buffer, this._position);
      this._position += read;
      return read;
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      RandomAccess.Write(lease.Handle, buffer, this._position);
      this._position += buffer.Length;
    }

    public override void Flush() {
      // WRITE_THROUGH forces the data past the OS cache, but not the drive's volatile cache
      // nor the file-size metadata for an append — FlushFileBuffers/fsync is the real barrier
      // the WAL's fsync-before-mutate ordering depends on (SAFE-ORDER / SAFE-FSYNC)
      if (writable)
        NativeMethods.FlushToDisk(lease.Handle);
    }

    public override long Seek(long offset, SeekOrigin origin) => this._position = origin switch {
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this.Length + offset,
      _ => offset,
    };

    public override void SetLength(long value) => RandomAccess.SetLength(lease.Handle, value);

    protected override void Dispose(bool disposing) {
      if (disposing && Interlocked.Exchange(ref this._disposed, 1) == 0)
        lease.Release();
      base.Dispose(disposing);
    }
  }

  private readonly HandlePool _pool = new();

  #endregion

  private static class NativeMethods {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = "GetDiskFreeSpaceEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "FlushFileBuffers")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("libc", SetLastError = true, EntryPoint = "fsync")]
    private static extern int Fsync(int fd);

    /// <summary>Forces buffered data AND file metadata to stable storage — the real durability barrier.</summary>
    public static void FlushToDisk(SafeFileHandle handle) {
      if (OperatingSystem.IsWindows()) {
        if (!FlushFileBuffers(handle))
          throw new IOException("FlushFileBuffers failed", Marshal.GetLastWin32Error());
        return;
      }

      // fsync on the underlying fd; SafeFileHandle wraps the int fd on Unix
      if (Fsync((int)handle.DangerousGetHandle()) != 0)
        throw new IOException("fsync failed", Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    private const uint _MOVEFILE_WRITE_THROUGH = 0x8;

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int Open(string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int Close(int fd);

    private const int _O_RDONLY = 0;
    private const int _EIO = 5;

    /// <summary>
    /// A rename that does not return until the NTFS log holding it is on disk. Without the flag the
    /// move sits in the cache like any other metadata, and a power cut can undo it after the caller
    /// has already been told the new name exists. It never replaces an existing target — the caller
    /// asks for that separately — so a failure reads exactly like <see cref="File.Move(string, string)"/>'s.
    /// </summary>
    public static void MoveWriteThrough(string from, string to) {
      if (MoveFileEx(_LongPath(from), _LongPath(to), _MOVEFILE_WRITE_THROUGH))
        return;

      // shaped like the exceptions File.Move raises, so Translate and the callers' catches see the same thing
      var error = Marshal.GetLastWin32Error();
      var message = $"Cannot move '{from}' to '{to}' (Win32 error {error})";
      throw error switch {
        2 /* ERROR_FILE_NOT_FOUND */ => new FileNotFoundException(message, from),
        3 /* ERROR_PATH_NOT_FOUND */ => new DirectoryNotFoundException(message),
        5 /* ERROR_ACCESS_DENIED */ => new UnauthorizedAccessException(message),
        _ => new IOException(message, unchecked((int)0x80070000 | error)),
      };
    }

    /// <summary>
    /// The extended-length form of a full path. File.Move adds it by itself where a path needs it;
    /// a direct call does not, and without it a pool with deep paths would fail every rename past
    /// 260 characters on a machine where long paths are not enabled system-wide.
    /// </summary>
    private static string _LongPath(string path) {
      if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        return path;

      path = path.Replace('/', '\\'); // the extended form is taken literally: no separator is translated

      return path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;
    }

    /// <summary>
    /// fsync on a DIRECTORY: what makes a rename, link or create inside it survive a power cut on
    /// Linux. Fsyncing a file makes its content durable, not the name pointing at it — the entry
    /// lives in the directory, and until the directory is flushed ext4/xfs may come back with the
    /// old name, the new one, or neither.
    ///
    /// Returns false when the directory cannot be opened or the filesystem does not support
    /// syncing a directory (some FUSE and network filesystems answer EINVAL): the rename has
    /// already happened, and failing it now would tell the caller its file is NOT there when it is.
    /// An I/O error is different — the disk could not store the name — and is reported.
    /// </summary>
    public static bool FlushDirectory(string path) {
      var fd = Open(path, _O_RDONLY);
      if (fd < 0)
        return false;

      try {
        if (Fsync(fd) == 0)
          return true;

        var errno = Marshal.GetLastPInvokeError();
        if (errno == _EIO)
          throw new IOException($"fsync of directory '{path}' failed", errno);

        return false;
      } finally {
        Close(fd);
      }
    }
  }

  private long _namespaceFlushes;

  /// <summary>
  /// How many renames this volume has made durable: directory fsyncs on Unix, write-through moves
  /// on Windows. A power cut cannot be simulated against a real disk, so this is how a test proves
  /// the barrier is actually taken on every publish path.
  /// </summary>
  public long NamespaceFlushes => Interlocked.Read(ref this._namespaceFlushes);

  /// <summary>
  /// Makes the entries of one directory durable (Unix); a no-op where the rename itself already was.
  ///
  /// Group commit per folder: a flush covers every change made in the folder before it STARTED, so a
  /// caller whose rename finished before a running flush began needs nothing more, and callers that
  /// arrive while one runs share the single flush that follows it. Many files published into one
  /// folder at once — what copying a tree of small files is — pay a handful of folder flushes instead
  /// of one each; measured on NVMe with sixteen writers, that was the difference between about 1,100
  /// and about 2,000 durable files a second.
  /// </summary>
  private void _FlushFolder(string folder) {
    if (OperatingSystem.IsWindows())
      return;

    var state = this._folderFlushes.GetOrAdd(folder, static _ => new());
    lock (state) {
      var mine = ++state.Changed;      // this caller's change is in the folder by now
      while (state.Flushed < mine) {
        if (state.Running) {
          Monitor.Wait(state);           // a flush is under way; it may have started too early to count
          continue;
        }

        state.Running = true;
        var covers = state.Changed;      // everything changed so far is in the folder before this flush starts
        Monitor.Exit(state);
        bool? flushed = null;
        try {
          flushed = NativeMethods.FlushDirectory(folder);
        } finally {
          Monitor.Enter(state);
          state.Running = false;
          // A filesystem that cannot sync a folder answers false; the rename stands either way, and
          // nobody waiting gains anything from asking again. A flush that THREW (an I/O error) covers
          // nothing: its caller reports the error, and whoever was waiting flushes for themselves.
          if (flushed.HasValue)
            state.Flushed = Math.Max(state.Flushed, covers);
          Monitor.PulseAll(state);
        }

        if (flushed == true)
          Interlocked.Increment(ref this._namespaceFlushes);
      }
    }
  }

  private sealed class _FolderFlush {
    public long Changed;
    public long Flushed;
    public bool Running;
  }

  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, _FolderFlush> _folderFlushes = new(StringComparer.Ordinal);

  /// <summary>Renames a file without replacing an existing target, durably on Windows (Unix flushes the folders after).</summary>
  private void _MoveFile(string from, string to) {
    if (!OperatingSystem.IsWindows()) {
      File.Move(from, to);
      return;
    }

    NativeMethods.MoveWriteThrough(from, to);
    Interlocked.Increment(ref this._namespaceFlushes);
  }

  /// <summary>
  /// Creates a folder and every missing parent so that they survive a power cut: each new entry
  /// lives in its parent directory, and a rename into a folder whose own entry was lost is lost
  /// with it. Only the levels actually created are flushed, so the common case — the folder is
  /// already there — costs one existence check.
  /// </summary>
  private void _EnsureFolderDurably(string folder) {
    if (Directory.Exists(folder))
      return;

    var created = new List<string>();
    for (var level = folder; !string.IsNullOrEmpty(level) && !Directory.Exists(level); level = System.IO.Path.GetDirectoryName(level))
      created.Add(level);

    Directory.CreateDirectory(folder);
    for (var i = created.Count - 1; i >= 0; --i)
      if (System.IO.Path.GetDirectoryName(created[i]) is { Length: > 0 } parent)
        this._FlushFolder(parent);
  }

  private readonly string _rootPath = System.IO.Path.GetFullPath(rootPath);

  public Guid MemberId { get; } = memberId;
  public string DisplayName { get; } = displayName;
  public string PhysicalVolumeId { get; } = physicalVolumeId;
  public string RootPath => this._rootPath;

  // IsOnline is queried on the hot path (ResolveCopies filters every copy by it, per VFS op). A
  // Directory.Exists on a UNC member whose host is down blocks for the SMB timeout — seconds —
  // each call, so cache the probe for a short window (loss is still noticed within ~1s).
  // The sentinel must sit one full TTL BELOW zero, because Environment.TickCount64 starts at zero
  // at boot and only grows. long.MinValue looks like the obvious "never probed" marker and is the
  // opposite: `now - long.MinValue` OVERFLOWS to a large negative value, which reads as "probed a
  // moment ago". So the first query returned the default `false` and never recorded a probe — the
  // member stayed offline forever. Mount refuses when no member is online, which made every mount
  // of a local pool fail with "No pool member is online" however plainly the directories existed.
  private long _onlineProbedTicks = -_ONLINE_TTL_MS - 1;
  private bool _onlineCached;
  private const long _ONLINE_TTL_MS = 1000;

  public bool IsOnline {
    get {
      var now = Environment.TickCount64;
      if (now - this._onlineProbedTicks < _ONLINE_TTL_MS)
        return this._onlineCached;

      this._onlineCached = Directory.Exists(this._rootPath);
      this._onlineProbedTicks = now;
      return this._onlineCached;
    }
  }

  private bool? _caseSensitive;

  /// <summary>
  /// Probed from the member's own marker file rather than assumed from the platform, and WITHOUT
  /// writing anything: every member carries <c>.drivebenderutility/member.json</c>, so asking
  /// whether the SHOUTED spelling of that same path also exists answers the question outright — a
  /// case-insensitive volume says yes, a case-sensitive one says no. Falls back to the platform
  /// default when the marker is not there to ask about (a member mid-creation, a stripped tree).
  /// </summary>
  public bool IsCaseSensitive => this._caseSensitive ??= this._ProbeCaseSensitivity();

  private bool _ProbeCaseSensitivity() {
    try {
      var folder = PoolPaths.UtilityFolderName;
      var marker = PoolPaths.MemberMarkerFileName;
      if (!File.Exists(System.IO.Path.Combine(this._rootPath, folder, marker)))
        return !OperatingSystem.IsWindows();

      return !File.Exists(System.IO.Path.Combine(this._rootPath, folder.ToUpperInvariant(), marker.ToUpperInvariant()));
    } catch (Exception) {
      return !OperatingSystem.IsWindows();
    }
  }

  /// <summary>
  /// Lets go of pooled OS handles the pool has finished with. Without this they live until the
  /// NEXT request touches the member — so an idle pool holds its members' files open forever and
  /// the volume can never be ejected.
  /// </summary>
  public void ReleaseIdleResources() => this._pool.EvictIdleNow();

  public BackendCaps Caps =>
    BackendCaps.RandomRead
    | BackendCaps.RandomWrite
    | BackendCaps.AtomicRename
    | BackendCaps.DurableFlush
    | BackendCaps.List
    | BackendCaps.Delete
    | BackendCaps.Timestamps
    // only where the host filesystem actually has a mode to keep; claiming it on Windows would make
    // the engine carry a permission it cannot store and report one it never set
    | (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? BackendCaps.Permissions : BackendCaps.None)
    | this._spaceCaps.Value;

  /// <summary>Hard links, block cloning and sparse ranges — what the filesystem here supports, found once (docs/SpaceSavings.md).</summary>
  private readonly Lazy<BackendCaps> _spaceCaps = new(() => SpaceNative.CapabilitiesOf(rootPath, Path.Combine(rootPath, PoolPaths.UtilityFolderName, "probe")));

  public int LinkCount(string relativePath, bool shadow)
    => (this.Caps & BackendCaps.HardLinks) == 0 ? 1 : this._Guard(() => SpaceNative.LinkCount(this._Resolve(relativePath, shadow)));

  public bool TryHardLink(string existingRelative, bool existingShadow, string newRelative, bool newShadow) {
    if ((this.Caps & BackendCaps.HardLinks) == 0)
      return false;

    return this._Guard(() => {
      var existing = this._Resolve(existingRelative, existingShadow);
      var target = this._Resolve(newRelative, newShadow);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);

      // The new name IS the existing file. A handle this member keeps pooled on the existing name
      // lacks delete sharing, and on Windows renaming ANY name of a file needs delete access to the
      // file — so renaming the link into place failed "being used by another process". A rename
      // retires handles on the names it touches; the other name of the same file is this one.
      this._pool.Invalidate(existing);
      return SpaceNative.TryHardLink(existing, target);
    });
  }

  public bool TryClone(string sourceRelative, bool sourceShadow, string targetRelative, bool targetShadow) {
    if ((this.Caps & BackendCaps.BlockClone) == 0)
      return false;

    return this._Guard(() => {
      var target = this._Resolve(targetRelative, targetShadow);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      return SpaceNative.TryClone(this._Resolve(sourceRelative, sourceShadow), target);
    });
  }

  public bool TryPunchHole(string relativePath, bool shadow, long offset, long length)
    => (this.Caps & BackendCaps.Sparse) != 0 && this._Guard(() => SpaceNative.TryPunchHole(this._Resolve(relativePath, shadow), offset, length));

  public long AllocatedBytes(string relativePath, bool shadow) => this._Guard(() => SpaceNative.AllocatedBytes(this._Resolve(relativePath, shadow)));

  /// <summary>
  /// A filesystem watcher over the whole member: what any program changes on the disk, the pool or
  /// not. Reads are not reported (last-access is not watched), so a job hashing files does not
  /// flag its own work. An overflowing watcher (a burst of changes, or on Linux the inotify limit)
  /// reports null: something changed, and it cannot say what.
  /// </summary>
  public IDisposable? WatchChanges(Action<string?> changed) {
    try {
      var watcher = new FileSystemWatcher(this._rootPath) {
        IncludeSubdirectories = true,
        InternalBufferSize = 64 * 1024,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
                       | NotifyFilters.Attributes | NotifyFilters.CreationTime | NotifyFilters.Security,
      };

      void Report(string fullPath) {
        var relative = System.IO.Path.GetRelativePath(this._rootPath, fullPath);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
          changed(PoolPaths.FromPhysical(relative));
      }

      watcher.Changed += (_, e) => Report(e.FullPath);
      watcher.Created += (_, e) => Report(e.FullPath);
      watcher.Deleted += (_, e) => Report(e.FullPath);
      watcher.Renamed += (_, e) => {
        Report(e.OldFullPath);
        Report(e.FullPath);
      };
      watcher.Error += (_, _) => changed(null);
      watcher.EnableRaisingEvents = true;
      return watcher;
    } catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException) {
      return null; // a member that cannot be watched is checked by its callers' own means only
    }
  }

  /// <summary>
  /// Capacity of the volume behind this member, or ZERO when it cannot be interrogated — the
  /// documented FR-STAT convention for "capacity unknown", which <see cref="PoolFileSystem.StatFs"/>
  /// excludes from the aggregate and placement treats as ineligible.
  ///
  /// These deliberately do not throw. They are PROPERTIES, and they are read from LINQ pipelines,
  /// from placement on the write path and from the background metrics tick — places where an
  /// exception is not expected and, in the case of a timer callback, kills the process outright.
  /// A member disappearing is the ordinary event this product exists to survive, so it must not
  /// arrive as an exception from a property that merely reports a number.
  /// </summary>
  public long BytesFree => (long)this._TryGetDiskSpace().free;

  public long BytesTotal => (long)this._TryGetDiskSpace().total;

  private (ulong free, ulong total) _TryGetDiskSpace() {
    try {
      return this._GetDiskSpace();
    } catch (Exception) {
      return (0, 0); // unreachable right now; IsOnline is what says so, not a throw from here
    }
  }

  private (ulong free, ulong total) _GetDiskSpace() {
    if (OperatingSystem.IsWindows()) {
      if (NativeMethods.GetDiskFreeSpaceEx(this._rootPath, out var available, out var total, out _))
        return (available, total);

      throw this._Offline();
    }

    // A member whose path has gone (the disk was pulled, the share dropped) makes DriveInfo throw
    // DriveNotFoundException — a raw System.IO exception, where the Windows branch above raises the
    // engine's own PoolFsException. Every engine catch is written against the engine's error model,
    // so the untranslated one sailed straight through all of them. Translated here so both
    // platforms fail the same way, and swallowed by the caller so neither fails loudly.
    try {
      var drive = new DriveInfo(this._rootPath);
      return ((ulong)drive.AvailableFreeSpace, (ulong)drive.TotalSize);
    } catch (Exception e) when (e is DriveNotFoundException or IOException or UnauthorizedAccessException or ArgumentException) {
      throw this._Offline();
    }
  }

  private string _Resolve(string relativePath, bool shadow)
    => this._Contain(PoolPaths.ToPhysical(relativePath, shadow), relativePath);

  private string _ResolveFolder(string relativeFolder, bool shadow)
    => this._Contain(PoolPaths.ToPhysicalFolder(relativeFolder, shadow), relativeFolder);

  /// <summary>
  /// Combines a pool-physical path with the member root and PROVES the result stays inside the
  /// root before any I/O touches it (SEC-PATH). <see cref="PoolPaths.Normalize"/> only strips
  /// <c>.</c>/<c>..</c>, so a drive-qualified or rooted path (e.g. <c>C:/x</c>, an ADS
  /// <c>f:stream</c>) would otherwise let <see cref="System.IO.Path.Combine"/> return a location
  /// OUTSIDE the pool — turning a pool write/delete into a write/delete anywhere on the machine.
  /// </summary>
  private string _Contain(string physical, string original) {
    var relative = physical.Replace('/', System.IO.Path.DirectorySeparatorChar);

    // a pool-relative path must never be rooted/drive-qualified ('C:\x', '\x'); Path.Combine would
    // otherwise return the rooted path and escape the member root entirely (SEC-PATH)
    if (System.IO.Path.IsPathRooted(relative))
      throw new PoolFsException(PoolFsError.InvalidArgument, $"Path escapes the member root: {original}");

    // join WITHOUT GetFullPath so trailing dots/spaces survive (GetFullPath strips them, collapsing
    // 'report.' onto 'report'); the extended-length form preserves them literally at the Win32 layer
    var joined = System.IO.Path.Combine(this._rootPath, relative);

    // containment is checked on a fully-normalised copy (belt-and-suspenders over Normalize's ../. strip)
    var root = System.IO.Path.TrimEndingDirectorySeparator(this._rootPath);
    var normalized = System.IO.Path.GetFullPath(joined);
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (!normalized.Equals(root, comparison) && !normalized.StartsWith(root + System.IO.Path.DirectorySeparatorChar, comparison))
      throw new PoolFsException(PoolFsError.InvalidArgument, $"Path escapes the member root: {original}");

    return _Extended(joined);
  }

  /// <summary>
  /// Prefixes an absolute Windows path with the extended-length form (\\?\ or \\?\UNC\) so Win32
  /// treats it LITERALLY: names past 260 chars work, and reserved device names (CON, NUL, AUX) and
  /// trailing dots/spaces stay distinct instead of being normalised away — otherwise 'report.' and
  /// 'report' collapse onto one physical file and silently overwrite each other (SAFE-BIGNAME).
  /// </summary>
  private static string _Extended(string fullPath) {
    if (!OperatingSystem.IsWindows() || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
      return fullPath;

    return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
      ? @"\\?\UNC\" + fullPath[2..] // \\server\share → \\?\UNC\server\share
      : @"\\?\" + fullPath;
  }

  private PoolFsException _Offline() => new(PoolFsError.Offline, $"Member '{this.DisplayName}' ({this._rootPath}) is offline");

  private T _Guard<T>(Func<T> operation) {
    try {
      return operation();
    } catch (Exception e) {
      throw Translate(e);
    }
  }

  private void _Guard(Action operation) => this._Guard<object?>(() => {
    operation();
    return null;
  });

  internal static Exception Translate(Exception e) => e switch {
    PoolFsException => e,
    FileNotFoundException or DirectoryNotFoundException => new PoolFsException(PoolFsError.NotFound, e.Message, e),
    UnauthorizedAccessException => new PoolFsException(PoolFsError.AccessDenied, e.Message, e),
    IOException io when io.HResult == unchecked((int)0x80070070) /* ERROR_DISK_FULL */ || io.HResult == unchecked((int)0x80070027) /* ERROR_HANDLE_DISK_FULL */ => new PoolFsException(PoolFsError.NoSpace, e.Message, e),
    IOException io when io.HResult == unchecked((int)0x800700B7) /* ERROR_ALREADY_EXISTS */ || io.HResult == unchecked((int)0x80070050) /* ERROR_FILE_EXISTS */ => new PoolFsException(PoolFsError.Exists, e.Message, e),
    IOException io when io.HResult == unchecked((int)0x80070091) /* ERROR_DIR_NOT_EMPTY */ => new PoolFsException(PoolFsError.NotEmpty, e.Message, e),
    IOException => new PoolFsException(PoolFsError.IoError, e.Message, e),
    _ => e,
  };

  public Stream OpenRead(string relativePath, bool shadow)
    => this._Guard(() => (Stream)new PooledStream(this._pool.Rent(this._Resolve(relativePath, shadow), writable: false, create: false), writable: false));

  public Stream OpenWrite(string relativePath, bool shadow, bool create) {
    return this._Guard(() => {
      var path = this._Resolve(relativePath, shadow);
      if (create)
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

      // pooled WRITE_THROUGH handle: positional writes are each a durability barrier (DurableFlush cap)
      return (Stream)new PooledStream(this._pool.Rent(path, writable: true, create), writable: true);
    });
  }

  public void Truncate(string relativePath, bool shadow, long length) => this._Guard(() => {
    var lease = this._pool.Rent(this._Resolve(relativePath, shadow), writable: true, create: false);
    try {
      RandomAccess.SetLength(lease.Handle, length);
    } finally {
      lease.Release();
    }
  });

  public void Delete(string relativePath, bool shadow) => this._Guard(() => {
    var path = this._Resolve(relativePath, shadow);
    var file = new FileInfo(path);
    if (!file.Exists)
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

    if ((file.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
      file.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);

    // invalidate AFTER the destructive op: a reader that opened between an early invalidate and
    // the delete would otherwise cache a handle to the (now removed) file and serve it forever
    this._pool.Invalidate(path);
    file.Delete();
    this._pool.Invalidate(path); // and again, in case a borrower re-cached during the delete
  });

  public void EnsureFolder(string relativeFolder, bool shadow) => this._Guard(() => {
    Directory.CreateDirectory(this._ResolveFolder(relativeFolder, shadow));
  });

  public void DeleteFolder(string relativeFolder, bool shadow) => this._Guard(() => {
    var path = this._ResolveFolder(relativeFolder, shadow);
    if (!Directory.Exists(path))
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {relativeFolder}");

    Directory.Delete(path, false);
  });

  public void RenameFolder(string fromRelativeFolder, string toRelativeFolder) => this._Guard(() => {
    var fromPath = this._ResolveFolder(fromRelativeFolder, false);
    var toPath = this._ResolveFolder(toRelativeFolder, false);
    if (!Directory.Exists(fromPath))
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {fromRelativeFolder}");
    if (Directory.Exists(toPath) || File.Exists(toPath))
      throw new PoolFsException(PoolFsError.Exists, $"Target already exists: {toRelativeFolder}");

    var toParent = System.IO.Path.GetDirectoryName(toPath)!;
    this._EnsureFolderDurably(toParent);
    this._pool.InvalidatePrefix(fromPath + System.IO.Path.DirectorySeparatorChar); // pooled keys move with the subtree
    if (OperatingSystem.IsWindows()) {
      NativeMethods.MoveWriteThrough(fromPath, toPath); // MoveFileEx moves folders too, durably
      Interlocked.Increment(ref this._namespaceFlushes);
    } else
      Directory.Move(fromPath, toPath);
    this._pool.InvalidatePrefix(fromPath + System.IO.Path.DirectorySeparatorChar); // and any re-cached during the move

    // as for a file: the moved folder's entry is durable only once both parents are flushed
    var fromParent = System.IO.Path.GetDirectoryName(fromPath)!;
    this._FlushFolder(toParent);
    if (!string.Equals(fromParent, toParent, StringComparison.Ordinal))
      this._FlushFolder(fromParent);
  });

  /// <summary>
  /// Windows only: <see cref="File.Replace(string, string, string?)"/> has no write-through form, so
  /// the replaced file is flushed afterwards — FlushFileBuffers commits the NTFS log up to that
  /// file's last change, which includes the rename. Best effort: a target that cannot be opened
  /// for writing (read-only) keeps the rename the replace already made, merely not forced out yet.
  /// </summary>
  private void _FlushReplaced(string finalPath) {
    if (!OperatingSystem.IsWindows())
      return;

    try {
      using var handle = File.OpenHandle(finalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
      NativeMethods.FlushToDisk(handle);
      Interlocked.Increment(ref this._namespaceFlushes);
    } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
      _ = e;
    }
  }

  public void AtomicReplace(string tempRelative, string finalRelative, bool shadow) => this._Guard(() => {
    var tempPath = this._Resolve(tempRelative, shadow);
    var finalPath = this._Resolve(finalRelative, shadow);
    if (!File.Exists(tempPath))
      throw new PoolFsException(PoolFsError.NotFound, $"Staged file not found: {tempRelative}");

    // A staged temp sits beside its final name, so the folder already exists — and creating it
    // anyway cost as much as the rename itself on every publish. Only a move into another folder
    // needs it.
    var finalFolder = System.IO.Path.GetDirectoryName(finalPath)!;
    var tempFolder = System.IO.Path.GetDirectoryName(tempPath)!;
    var sameFolder = string.Equals(finalFolder, tempFolder, StringComparison.OrdinalIgnoreCase);
    if (!sameFolder)
      this._EnsureFolderDurably(finalFolder);

    this._pool.Invalidate(tempPath);
    this._pool.Invalidate(finalPath);

    // Move first: the common case is a brand-new name, and asking whether the target exists before
    // every rename was a second filesystem round trip to learn what the rename reports anyway. A
    // target that does exist fails the move with "already exists" and is then REPLACED, keeping the
    // replace semantics (attributes and security carried over) for exactly the case that needs them.
    try {
      this._MoveFile(tempPath, finalPath);
    } catch (IOException e) when (File.Exists(finalPath) && File.Exists(tempPath)) {
      _ = e;
      File.Replace(tempPath, finalPath, null, true);
      this._FlushReplaced(finalPath);
    }

    // The caller treats the publish as done once this returns — the staged temp is gone from its
    // point of view, and mount recovery deletes any temp it finds. On Linux the rename is only in
    // the page cache until the directory is flushed, so a power cut inside the filesystem's commit
    // window used to leave the fsynced temp under its old name and no final name: recovery then
    // deleted the temp, and a file the caller was told is safe was gone.
    this._FlushFolder(finalFolder);
    if (!sameFolder)
      this._FlushFolder(tempFolder);
    // invalidate again AFTER: a reader that re-cached the old finalPath during the replace must
    // not keep serving the pre-replace file (its handle stays valid under FileShare.Delete)
    this._pool.Invalidate(finalPath);
    this._pool.Invalidate(tempPath);
  });

  public FileMeta? Stat(string relativePath, bool shadow) => this._Guard<FileMeta?>(() => {
    var path = this._Resolve(relativePath, shadow);
    var file = new FileInfo(path);
    if (file.Exists)
      // while a pooled write handle is open, the handle's length is authoritative — NTFS
      // directory metadata (what FileInfo reads) lags behind write-through data
      return new FileMeta(this._pool.TryGetWriteLength(path, out var live) ? live : file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, file.Attributes, _ModeOf(path));

    var directory = new DirectoryInfo(path);
    if (directory.Exists)
      return new FileMeta(0, directory.CreationTimeUtc, directory.LastWriteTimeUtc, directory.Attributes, _ModeOf(path));

    return null;
  });

  public bool FileExists(string relativePath, bool shadow) => File.Exists(this._Resolve(relativePath, shadow));
  public bool FolderExists(string relativeFolder, bool shadow) => Directory.Exists(this._ResolveFolder(relativeFolder, shadow));

  public IEnumerable<VolumeEntry> List(string relativeFolder, bool shadow) {
    var path = this._ResolveFolder(relativeFolder, shadow);
    var directory = new DirectoryInfo(path);
    if (!directory.Exists)
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {relativeFolder}");

    // materialise inside the guard so a drive yanked mid-enumeration surfaces as a
    // PoolFsException (the error model every engine catch relies on), not a raw IOException
    return this._Guard(() => directory.EnumerateFileSystemInfos().Select(item => item switch {
      // live length from the pooled write handle when one is open — directory metadata lags it
      FileInfo f => new VolumeEntry(f.Name, false, this._pool.TryGetWriteLength(f.FullName, out var live) ? live : f.Length, f.LastWriteTimeUtc),
      _ => new VolumeEntry(item.Name, true, 0, item.LastWriteTimeUtc),
    }).ToArray());
  }

  /// <summary>
  /// The stored POSIX mode, or null off Unix where the concept does not apply.
  ///
  /// Read from the underlying file rather than kept beside it, because that is where it already
  /// lives: the pool stores real files on real filesystems, so the mode persists, survives a
  /// remount and is what an operator sees if they look at the member directly.
  /// </summary>
  private static UnixFileMode? _ModeOf(string path) {
    if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
      return null;

    try {
      return File.GetUnixFileMode(path);
    } catch (Exception) {
      return null; // an unreadable mode is not a reason to fail the stat the caller asked for
    }
  }

  public void SetPermissions(string relativePath, bool shadow, UnixFileMode mode) {
    if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
      return;

    this._Guard(() => File.SetUnixFileMode(this._Resolve(relativePath, shadow), mode));
  }

  public void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc) => this._Guard(() => {
    var path = this._Resolve(relativePath, shadow);
    if (creationTimeUtc is { } created)
      File.SetCreationTimeUtc(path, created);
    if (lastWriteTimeUtc is { } modified)
      File.SetLastWriteTimeUtc(path, modified);
  });

  // Directory.*, not File.*: on Windows File.Set*Time opens the path without backup semantics and
  // cannot open a directory at all, so it throws for exactly the case this exists for
  public void SetFolderTimestamps(string relativeFolder, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc) => this._Guard(() => {
    var path = this._ResolveFolder(relativeFolder, false);
    if (creationTimeUtc is { } created)
      Directory.SetCreationTimeUtc(path, created);
    if (lastWriteTimeUtc is { } modified)
      Directory.SetLastWriteTimeUtc(path, modified);
  });

}

/// <summary>Backend registration for local/UNC members (scheme "file"/"unc").</summary>
public sealed class LocalVolumeIOBackend(IHostEnvironment host) : IVolumeIOBackend {
  public string Scheme => "file";

  public BackendCaps Caps =>
    BackendCaps.RandomRead
    | BackendCaps.RandomWrite
    | BackendCaps.AtomicRename
    | BackendCaps.DurableFlush
    | BackendCaps.List
    | BackendCaps.Delete
    | BackendCaps.Timestamps
    // only where the host filesystem actually has a mode to keep; claiming it on Windows would make
    // the engine carry a permission it cannot store and report one it never set
    | (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? BackendCaps.Permissions : BackendCaps.None);

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials)
    => new LocalVolumeIO(member.MemberId, member.DisplayName, member.Path, host.GetVolumeIdentity(member.Path).PhysicalVolumeId);
}
