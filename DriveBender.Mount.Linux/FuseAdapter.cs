using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using FuseDotNet;
using FuseDotNet.Extensions;
using LTRData.Extensions.Native.Memory;

namespace DivisonM.Mount.Linux;

/// <summary>
/// The FUSE platform adapter (§4.1): a thin translation between libfuse callbacks and
/// <see cref="IPoolFileSystem"/>. No pool logic lives here (NFR-PORT) — errors map from
/// <see cref="PoolFsError"/> to errno, paths and stats convert, nothing more.
/// </summary>
public sealed class FuseAdapter(IPoolFileSystem pool) : IFuseOperations {

  private static string _ToPoolPath(ReadOnlyNativeMemory<byte> fileNamePtr)
    => FuseHelper.GetString(fileNamePtr).TrimStart('/');

  private static PosixResult _Translate(PoolFsException e) => e.Error switch {
    PoolFsError.NotFound => PosixResult.ENOENT,
    PoolFsError.AccessDenied => PosixResult.EACCES,
    PoolFsError.Exists => PosixResult.EEXIST,
    PoolFsError.NotEmpty => new PosixResult(39), // ENOTEMPTY on Linux
    PoolFsError.NoSpace => PosixResult.ENOSPC,
    PoolFsError.StaleHandle => PosixResult.EBADF,
    PoolFsError.NotSupported => PosixResult.ENOTSUP,
    PoolFsError.InvalidArgument => PosixResult.EINVAL,
    PoolFsError.NotADirectory => PosixResult.ENOTDIR,
    PoolFsError.IsADirectory => PosixResult.EISDIR,
    _ => PosixResult.EIO,
  };

  private static FuseFileStat _ToStat(FileMeta meta) {
    var stat = new FuseFileStat {
      st_size = meta.Length,
      st_nlink = meta.IsDirectory ? 2 : 1,
      st_mode = meta.IsDirectory
        ? PosixFileMode.Directory | PosixFileMode.OwnerAll | PosixFileMode.GroupReadExecute | PosixFileMode.OthersReadExecute
        : PosixFileMode.Regular | PosixFileMode.OwnerReadWrite | PosixFileMode.GroupRead | PosixFileMode.OthersRead,
      st_blksize = 4096,
      st_blocks = (meta.Length + 511) / 512,
      st_uid = NativeUid,
      st_gid = NativeGid,
    };

    if (meta.LastWriteTimeUtc != DateTime.MinValue) {
      stat.st_mtim = meta.LastWriteTimeUtc;
      stat.st_atim = meta.LastWriteTimeUtc;
      stat.st_ctim = meta.LastWriteTimeUtc;
    }

    if (meta.CreationTimeUtc != DateTime.MinValue)
      stat.st_birthtim = meta.CreationTimeUtc;

    return stat;
  }

  internal static uint NativeUid { get; set; }
  internal static uint NativeGid { get; set; }

  public void Init(ref FuseConnInfo fuse_conn_info) {
  }

  public PosixResult GetAttr(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo) {
    stat = default;
    try {
      stat = _ToStat(pool.GetAttributes(_ToPoolPath(fileNamePtr)));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult Access(ReadOnlyNativeMemory<byte> fileNamePtr, PosixAccessMode mask) {
    try {
      pool.GetAttributes(_ToPoolPath(fileNamePtr));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult OpenDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) {
    try {
      var meta = pool.GetAttributes(_ToPoolPath(fileNamePtr));
      return meta.IsDirectory ? PosixResult.Success : PosixResult.ENOTDIR;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult ReadDir(ReadOnlyNativeMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries, ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags) {
    entries = [];
    try {
      var listing = pool.ReadDirectory(_ToPoolPath(fileNamePtr));
      entries = FuseHelper.DotEntries.Concat(listing.Select(entry => new FuseDirEntry(
        entry.Name,
        0,
        0,
        _ToStat(new(entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Kind == NodeKind.Directory ? FileAttributes.Directory : FileAttributes.Normal)))));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult ReleaseDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.Success;

  public PosixResult FSyncDir(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => PosixResult.Success;

  public PosixResult Open(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) {
    try {
      var wantsWrite = (fileInfo.flags & PosixOpenFlags.AccessModes) != PosixOpenFlags.Read;
      var handle = pool.Open(_ToPoolPath(fileNamePtr), wantsWrite ? AccessMode.ReadWrite : AccessMode.Read, ShareMode.Read | ShareMode.Write);
      if ((fileInfo.flags & PosixOpenFlags.Truncate) != 0)
        pool.SetLength(handle, 0);

      fileInfo.Context = handle;
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult Create(ReadOnlyNativeMemory<byte> fileNamePtr, int mode, ref FuseFileInfo fileInfo) {
    try {
      var handle = pool.Create(_ToPoolPath(fileNamePtr), NodeKind.File, CreateFlags.None);
      fileInfo.Context = handle;
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  private NodeHandle _HandleOf(ref FuseFileInfo fileInfo)
    => fileInfo.Context is NodeHandle handle ? handle : NodeHandle.Invalid;

  public PosixResult Read(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> buffer, long position, out int readLength, ref FuseFileInfo fileInfo) {
    readLength = 0;
    try {
      var handle = this._HandleOf(ref fileInfo);
      var transient = handle == NodeHandle.Invalid;
      if (transient)
        handle = pool.Open(_ToPoolPath(fileNamePtr), AccessMode.Read, ShareMode.Read | ShareMode.Write);

      try {
        readLength = pool.Read(handle, buffer.Span, position);
      } finally {
        if (transient)
          pool.Close(handle);
      }

      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult Write(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> buffer, long position, out int writtenLength, ref FuseFileInfo fileInfo) {
    writtenLength = 0;
    try {
      var handle = this._HandleOf(ref fileInfo);
      var transient = handle == NodeHandle.Invalid;
      if (transient)
        handle = pool.Open(_ToPoolPath(fileNamePtr), AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);

      try {
        var append = (fileInfo.flags & PosixOpenFlags.Append) != 0;
        writtenLength = pool.Write(handle, buffer.Span, position, append ? WriteMode.Append : WriteMode.Normal);
      } finally {
        if (transient)
          pool.Close(handle);
      }

      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult Flush(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) {
    try {
      var handle = this._HandleOf(ref fileInfo);
      if (handle != NodeHandle.Invalid)
        pool.Flush(handle);

      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult FSync(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => this.Flush(fileNamePtr, ref fileInfo);

  public PosixResult Release(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) {
    try {
      var handle = this._HandleOf(ref fileInfo);
      if (handle != NodeHandle.Invalid) {
        pool.Close(handle);
        fileInfo.Context = null;
      }

      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult Truncate(ReadOnlyNativeMemory<byte> fileNamePtr, long size) {
    try {
      var handle = pool.Open(_ToPoolPath(fileNamePtr), AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
      try {
        pool.SetLength(handle, size);
      } finally {
        pool.Close(handle);
      }

      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult Unlink(ReadOnlyNativeMemory<byte> fileNamePtr) {
    try {
      pool.Unlink(_ToPoolPath(fileNamePtr));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult MkDir(ReadOnlyNativeMemory<byte> fileNamePtr, PosixFileMode mode) {
    try {
      pool.MakeDir(_ToPoolPath(fileNamePtr));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult RmDir(ReadOnlyNativeMemory<byte> fileNamePtr) {
    try {
      pool.RemoveDir(_ToPoolPath(fileNamePtr));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) {
    try {
      pool.Rename(_ToPoolPath(from), _ToPoolPath(to), RenameFlags.ReplaceExisting);
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public PosixResult UTime(ReadOnlyNativeMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fileInfo) {
    try {
      pool.SetAttributes(_ToPoolPath(fileNamePtr), new(LastWriteTimeUtc: mtime.IsOmit || mtime.IsPseudoNow ? null : mtime.ToDateTime().UtcDateTime));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return e.Error == PoolFsError.NotSupported ? PosixResult.Success : _Translate(e);
    }
  }

  public PosixResult StatFs(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseVfsStat statvfs) {
    statvfs = default;
    try {
      var stats = pool.StatFs();
      const ulong blockSize = 4096;
      statvfs.f_bsize = blockSize;
      statvfs.f_frsize = blockSize;
      statvfs.f_blocks = (ulong)stats.BytesTotal / blockSize;
      statvfs.f_bfree = (ulong)stats.BytesFree / blockSize;
      statvfs.f_bavail = (ulong)stats.BytesFree / blockSize;
      statvfs.f_namemax = 255;
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  // pass-through semantics not represented by the pool model (FR-LINK: deterministic NotSupported)
  public PosixResult ReadLink(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> target) => PosixResult.ENOTSUP;
  public PosixResult Link(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.ENOTSUP;
  public PosixResult SymLink(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.ENOTSUP;
  public PosixResult IoCtl(ReadOnlyNativeMemory<byte> fileNamePtr, int cmd, nint arg, ref FuseFileInfo fileInfo, FuseIoctlFlags flags, nint data) => PosixResult.ENOTSUP;
  public PosixResult FAllocate(NativeMemory<byte> fileNamePtr, FuseAllocateMode mode, long offset, long length, ref FuseFileInfo fileInfo) => PosixResult.ENOTSUP;

  // ownership/permissions passthrough is best-effort (FR-PERMS): the pool presents a documented default mode
  public PosixResult ChMod(NativeMemory<byte> fileNamePtr, PosixFileMode mode) => PosixResult.Success;
  public PosixResult ChOwn(NativeMemory<byte> fileNamePtr, int uid, int gid) => PosixResult.Success;

  public void Dispose() {
  }

}

/// <summary>Hosts a pool behind libfuse at a directory mountpoint (FR-MOUNT-FSTAB, §6.12).</summary>
public static class LinuxFuseMountHost {

  static LinuxFuseMountHost() {
    // distros ship only the versioned SONAME (libfuse3.so.3); the unversioned name needs the -dev package
    System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Fuse).Assembly, (name, _, _) =>
      name is "fuse3" or "libfuse3" && System.Runtime.InteropServices.NativeLibrary.TryLoad("libfuse3.so.3", out var handle)
        ? handle
        : IntPtr.Zero);
  }

  public static bool IsFuseAvailable() => OperatingSystem.IsLinux() && File.Exists("/dev/fuse");

  /// <summary>Mounts and blocks until the filesystem is unmounted (umount/fusermount3, Ctrl+C, or a registry stop request).</summary>
  public static int Run(PoolFileSystem fs, string target, bool readOnly, Action? onMounted = null, Func<bool>? stopRequested = null, Action? onUnmounted = null) {
    FuseAdapter.NativeUid = _GetId("-u");
    FuseAdapter.NativeGid = _GetId("-g");

    fs.Mount(new(target, readOnly));
    var scheduler = fs.CreateScheduler();
    var registered = false;
    using var pump = new Timer(_ => {
      scheduler.Pump();
      if (!registered && (System.IO.Directory.Exists(target) && _IsMounted(target))) {
        registered = true;
        onMounted?.Invoke();
      }

      // another dbmount asked for a clean unmount → detach; libfuse returns from Mount()
      if (stopRequested?.Invoke() == true)
        Unmount(target);
    }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    Console.CancelKeyPress += (_, e) => {
      e.Cancel = true;
      Unmount(target);
    };

    var options = "fsname=drivebender,subtype=drivebender,default_permissions" + (readOnly ? ",ro" : "");
    var adapter = new FuseAdapter(fs);
    DriveBender.Logger($"Mounting pool at '{target}' via FUSE (unmount with: umount {target} or dbmount unmount {target})");
    var args = new List<string> { "dbmount", target, "-f", "-o", options };
    if (Environment.GetEnvironmentVariable("DBMOUNT_FUSE_DEBUG") == "1")
      args.Add("-d");

    try {
      // "-f": stay in the foreground so this process owns the lifecycle
      adapter.Mount(args);
    } finally {
      scheduler.Quiesce();
      fs.Unmount(); // clean unmount flushes everything (FR-CLEAN-UNMOUNT)
      onUnmounted?.Invoke();
    }

    return 0;
  }

  private static bool _IsMounted(string target) {
    try {
      foreach (var line in File.ReadLines("/proc/mounts")) {
        var parts = line.Split(' ');
        if (parts.Length >= 2 && parts[1] == target)
          return true;
      }
    } catch (IOException) {
    }

    return false;
  }

  public static void Unmount(string target) {
    if (Fuse.TryUnmount(target, out _))
      return;

    // unprivileged mounts unmount through fusermount3
    try {
      System.Diagnostics.Process.Start("fusermount3", ["-u", target])?.WaitForExit();
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]fusermount3 -u {target} failed: {e.Message}");
    }
  }

  private static uint _GetId(string flag) {
    try {
      var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("id", flag) { RedirectStandardOutput = true });
      var output = process!.StandardOutput.ReadToEnd().Trim();
      process.WaitForExit();
      return uint.TryParse(output, out var id) ? id : 0;
    } catch (Exception) {
      return 0;
    }
  }

}
