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

    private readonly Dictionary<string, HandleLease> _leases = new(StringComparer.OrdinalIgnoreCase);
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
        foreach (var key in this._leases.Keys.Where(k => k.AsSpan(2).StartsWith(physicalPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
          this._Retire(key);
    }

    private void _Retire(string key) {
      if (!this._leases.Remove(key, out var lease))
        return;

      lease.Evicted = true;
      lease.Release(); // pool ref — closes once the last borrower returns it
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

    public override int Read(byte[] buffer, int offset, int count) {
      var read = RandomAccess.Read(lease.Handle, buffer.AsSpan(offset, count), this._position);
      this._position += read;
      return read;
    }

    public override void Write(byte[] buffer, int offset, int count) {
      RandomAccess.Write(lease.Handle, buffer.AsSpan(offset, count), this._position);
      this._position += count;
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
  }

  private readonly string _rootPath = System.IO.Path.GetFullPath(rootPath);

  public Guid MemberId { get; } = memberId;
  public string DisplayName { get; } = displayName;
  public string PhysicalVolumeId { get; } = physicalVolumeId;
  public string RootPath => this._rootPath;

  // IsOnline is queried on the hot path (ResolveCopies filters every copy by it, per VFS op). A
  // Directory.Exists on a UNC member whose host is down blocks for the SMB timeout — seconds —
  // each call, so cache the probe for a short window (loss is still noticed within ~1s).
  private long _onlineProbedTicks = long.MinValue;
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

  public BackendCaps Caps =>
    BackendCaps.RandomRead
    | BackendCaps.RandomWrite
    | BackendCaps.AtomicRename
    | BackendCaps.DurableFlush
    | BackendCaps.List
    | BackendCaps.Delete
    | BackendCaps.Timestamps;

  public long BytesFree => (long)this._GetDiskSpace().free;
  public long BytesTotal => (long)this._GetDiskSpace().total;

  private (ulong free, ulong total) _GetDiskSpace() {
    if (OperatingSystem.IsWindows()) {
      if (NativeMethods.GetDiskFreeSpaceEx(this._rootPath, out var available, out var total, out _))
        return (available, total);

      throw this._Offline();
    }

    var drive = new DriveInfo(this._rootPath);
    return ((ulong)drive.AvailableFreeSpace, (ulong)drive.TotalSize);
  }

  private string _Resolve(string relativePath, bool shadow)
    => System.IO.Path.Combine(this._rootPath, PoolPaths.ToPhysical(relativePath, shadow).Replace('/', System.IO.Path.DirectorySeparatorChar));

  private string _ResolveFolder(string relativeFolder, bool shadow)
    => System.IO.Path.Combine(this._rootPath, PoolPaths.ToPhysicalFolder(relativeFolder, shadow).Replace('/', System.IO.Path.DirectorySeparatorChar));

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

    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(toPath)!);
    this._pool.InvalidatePrefix(fromPath + System.IO.Path.DirectorySeparatorChar); // pooled keys move with the subtree
    Directory.Move(fromPath, toPath);
    this._pool.InvalidatePrefix(fromPath + System.IO.Path.DirectorySeparatorChar); // and any re-cached during the move
  });

  public void AtomicReplace(string tempRelative, string finalRelative, bool shadow) => this._Guard(() => {
    var tempPath = this._Resolve(tempRelative, shadow);
    var finalPath = this._Resolve(finalRelative, shadow);
    if (!File.Exists(tempPath))
      throw new PoolFsException(PoolFsError.NotFound, $"Staged file not found: {tempRelative}");

    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(finalPath)!);
    this._pool.Invalidate(tempPath);
    this._pool.Invalidate(finalPath);
    if (File.Exists(finalPath))
      File.Replace(tempPath, finalPath, null, true);
    else
      File.Move(tempPath, finalPath);
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
      return new FileMeta(this._pool.TryGetWriteLength(path, out var live) ? live : file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, file.Attributes);

    var directory = new DirectoryInfo(path);
    if (directory.Exists)
      return new FileMeta(0, directory.CreationTimeUtc, directory.LastWriteTimeUtc, directory.Attributes);

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

  public void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc) => this._Guard(() => {
    var path = this._Resolve(relativePath, shadow);
    if (creationTimeUtc is { } created)
      File.SetCreationTimeUtc(path, created);
    if (lastWriteTimeUtc is { } modified)
      File.SetLastWriteTimeUtc(path, modified);
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
    | BackendCaps.Timestamps;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials)
    => new LocalVolumeIO(member.MemberId, member.DisplayName, member.Path, host.GetVolumeIdentity(member.Path).PhysicalVolumeId);
}
