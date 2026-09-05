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

  /// <summary>
  /// Last resort for an exception the engine did not classify: log it and answer EIO.
  ///
  /// Every operation here used to catch <see cref="PoolFsException"/> and nothing else, so anything
  /// the engine did not wrap — an <see cref="IOException"/> from a member's own file handle, a
  /// disposed object, a null reference in a path the tests have not reached — was thrown straight
  /// back through the libfuse callback into native code. That is an exception crossing a boundary
  /// that cannot carry it: the outcome is undefined, the operation gets no defined errno, and
  /// nothing at all is written down. A dying disk surfacing an IOException mid-read is not exotic.
  ///
  /// EIO is the honest answer — the filesystem could not complete the operation — and the log line
  /// is the part that matters, because it is the only record of what actually happened.
  /// </summary>
  private static PosixResult _Unexpected(Exception e) {
    DriveBender.Logger($"[Warning]Unhandled {e.GetType().Name} in a filesystem operation, answered EIO: {e}");
    return PosixResult.EIO;
  }

  /// <summary>
  /// Maps an engine error onto an errno, and LOGS the ones that are not part of normal operation.
  ///
  /// Everything below has a specific errno except the fallback, and the fallback is EIO — "the
  /// filesystem could not do this and cannot tell you why". An application sees only that number,
  /// so if the pool does not write down what it actually was, the reason is gone: the mount log
  /// showed a healthy pool while reads were failing. The routine outcomes (a missing file, an
  /// existing one, a non-empty directory) stay silent, because they are answers rather than faults.
  /// </summary>
  private static PosixResult _Translate(PoolFsException e) {
    // The routine outcomes are ANSWERS, not faults: a missing file, one that already exists, a
    // directory that is not empty. Everything else means the pool could not do what was asked, and
    // most of those collapse into EIO — a number that tells the application nothing at all. If the
    // pool does not write down what it actually was, the reason is gone: reads were failing here
    // while the mount log showed a perfectly healthy pool, and finding out why took a debugger.
    if (e.Error is not (PoolFsError.NotFound or PoolFsError.Exists or PoolFsError.NotEmpty
        or PoolFsError.AccessDenied or PoolFsError.IsADirectory or PoolFsError.NotADirectory
        or PoolFsError.InvalidArgument or PoolFsError.NotSupported))
      DriveBender.Logger($"[Warning]{e.Error} surfaced to the application: {e.Message}");

    return _Map(e);
  }

  private static PosixResult _Map(PoolFsException e) => e.Error switch {
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
    } catch (Exception e) {
      return _Unexpected(e);
    }
  }

  public PosixResult Access(ReadOnlyNativeMemory<byte> fileNamePtr, PosixAccessMode mask) {
    try {
      pool.GetAttributes(_ToPoolPath(fileNamePtr));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      return _Unexpected(e);
    }
  }

  public PosixResult OpenDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) {
    try {
      var meta = pool.GetAttributes(_ToPoolPath(fileNamePtr));
      return meta.IsDirectory ? PosixResult.Success : PosixResult.ENOTDIR;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      return _Unexpected(e);
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
    } catch (Exception e) {
      return _Unexpected(e);
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
    } catch (Exception e) {
      return _Unexpected(e);
    }
  }

  public PosixResult Create(ReadOnlyNativeMemory<byte> fileNamePtr, int mode, ref FuseFileInfo fileInfo) {
    try {
      var handle = pool.Create(_ToPoolPath(fileNamePtr), NodeKind.File, CreateFlags.None);
      fileInfo.Context = handle;
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      return _Unexpected(e);
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
    } catch (Exception e) {
      return _Unexpected(e);
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
    } catch (Exception e) {
      return _Unexpected(e);
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
    } catch (Exception e) {
      return _Unexpected(e);
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
    } catch (Exception e) {
      return _Unexpected(e);
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
    } catch (Exception e) {
      return _Unexpected(e);
    }
  }

  public PosixResult Unlink(ReadOnlyNativeMemory<byte> fileNamePtr) {
    try {
      pool.Unlink(_ToPoolPath(fileNamePtr));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      return _Unexpected(e);
    }
  }

  public PosixResult MkDir(ReadOnlyNativeMemory<byte> fileNamePtr, PosixFileMode mode) {
    try {
      pool.MakeDir(_ToPoolPath(fileNamePtr));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      return _Unexpected(e);
    }
  }

  public PosixResult RmDir(ReadOnlyNativeMemory<byte> fileNamePtr) {
    try {
      pool.RemoveDir(_ToPoolPath(fileNamePtr));
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      return _Unexpected(e);
    }
  }

  public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) {
    try {
      pool.Rename(_ToPoolPath(from), _ToPoolPath(to), RenameFlags.ReplaceExisting);
      return PosixResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      return _Unexpected(e);
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
    } catch (Exception e) {
      return _Unexpected(e);
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
    // Resolve libfuse3 across releases: distros ship only the versioned SONAME, and it changed with
    // the library ABI — libfuse3.so.3 up to 3.16, libfuse3.so.4 from 3.17 (e.g. current Arch). The
    // unversioned libfuse3.so only exists with the -dev package. Try each so a runtime-only install
    // of ANY modern libfuse3 resolves instead of failing with DllNotFoundException at mount time.
    System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Fuse).Assembly, (name, _, _) => {
      if (name is not ("fuse3" or "libfuse3"))
        return IntPtr.Zero;

      foreach (var candidate in new[] { "libfuse3.so.3", "libfuse3.so.4", "libfuse3.so" })
        if (System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out var handle))
          return handle;

      return IntPtr.Zero;
    });
  }

  public static bool IsFuseAvailable() => OperatingSystem.IsLinux() && File.Exists("/dev/fuse");

  /// <summary>Mounts and blocks until the filesystem is unmounted (umount/fusermount3, Ctrl+C, or a registry stop request).</summary>
  /// <summary>
  /// How long the pool can be mounted and USABLE while <c>dbmount status</c> and
  /// <c>dbmount unmount</c> still believe it does not exist.
  ///
  /// The registry entry is what those two verbs look the pool up in, and on this platform it is
  /// written from the pump the first time the mount is seen in <c>/proc/mounts</c> — the Windows
  /// host registers inline, the moment its own Mount() call returns, and has no such window. At a
  /// one-second poll the window was long enough to hit constantly: mount a pool and unmount it
  /// straight away and the unmount answers "No mounted pool matches", exits non-zero, and LEAVES IT
  /// MOUNTED — with the process still serving it. A script that mounts, does its work and unmounts
  /// simply fails, and the end-to-end harness hit it on nearly every pool it built.
  /// </summary>
  private static readonly TimeSpan _AWAITING_MOUNT_TICK = TimeSpan.FromMilliseconds(50);

  /// <summary>Once registered there is nothing to race, and the pump is back to its ordinary cadence.</summary>
  private static readonly TimeSpan _REGISTERED_TICK = TimeSpan.FromSeconds(1);

  /// <summary>How often the stop watcher looks for an unmount request; a file-exists check, so it can be brisk.</summary>
  private static readonly TimeSpan _STOP_POLL = TimeSpan.FromMilliseconds(250);

  public static int Run(PoolFileSystem fs, string target, bool readOnly, Action? onMounted = null, Func<bool>? stopRequested = null, Action? onUnmounted = null, Action? onTick = null) {
    FuseAdapter.NativeUid = _GetId("-u");
    FuseAdapter.NativeGid = _GetId("-g");

    fs.Mount(new(target, readOnly));
    var scheduler = fs.CreateScheduler();
    var registered = false;

    // NON-REENTRANT AND NON-FATAL, matching the WinFsp host — this pump had neither, and both
    // gaps were real. An unhandled exception in a Timer callback KILLS THE PROCESS: pulling a
    // member out from under a live mount made the very next tick throw while reading the vanished
    // member's free space, and the FUSE session died with it. Everything the user had open on the
    // pool then failed with ENOTCONN, from one disk going away — which is the exact opposite of
    // what a redundant pool is for. And the timer was PERIODIC, so a slow tick (a live reload, a
    // big metrics publish) overlapped the next one on another thread-pool thread and ran
    // Pump()/metrics/reload concurrently against the engine; it is armed for a single shot and
    // re-armed at the end of each tick instead.
    Timer? pump = null;
    pump = new Timer(_ => {
      try {
        scheduler.Pump();
        if (!registered && (System.IO.Directory.Exists(target) && _IsMounted(target))) {
          registered = true;
          onMounted?.Invoke();
        }

        if (registered)
          onTick?.Invoke(); // periodic metrics snapshot for the serve daemon

      } catch (Exception e) {
        DriveBender.Logger($"[Warning]background pump tick failed: {e.Message}");
      } finally {
        try {
          // Until the mount is REGISTERED the tick is the only thing that can register it, so it
          // runs often; afterwards once a second is plenty. libfuse offers no "you are mounted now"
          // callback, so noticing it is a poll, and the poll interval IS the window during which
          // the pool is usable but invisible to `dbmount status` and `dbmount unmount`.
          pump?.Change(registered ? _REGISTERED_TICK : _AWAITING_MOUNT_TICK, Timeout.InfiniteTimeSpan);
        } catch (ObjectDisposedException) {
          // the mount is shutting down and the timer is already gone
        }
      }
    }, null, _AWAITING_MOUNT_TICK, Timeout.InfiniteTimeSpan);

    using var pumpLifetime = pump;

    // A DEDICATED watcher, not a step in the pump tick, which is where this check used to live.
    //
    // The pump is deliberately non-reentrant: it re-arms only at the END of each tick. One unit of
    // its work is a whole file, so a drain down a member held to 64 KiB/s keeps a single tick busy
    // for minutes — and the stop check sat AFTER that work, so the unmount request was not even
    // NOTICED until the copy the operator had throttled finished. The verb waited twenty seconds,
    // reported the mount still active, and the process had to be killed. Rate-limiting a disk must
    // not cost the ability to shut down cleanly, and this poll is far too cheap to be behind the
    // work it has to interrupt.
    var detaching = 0;
    using var stopWatcher = new Timer(_ => {
      if (stopRequested?.Invoke() != true || Interlocked.Exchange(ref detaching, 1) != 0)
        return;

      fs.AbortBackgroundWork(); // a copy already in flight stops at its next chunk
      Unmount(target); // libfuse returns from Mount()
    }, null, _STOP_POLL, _STOP_POLL);

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
      // libfuse's setup path (the mount syscall / its fusermount3 fork+waitpid, and the first reads
      // on /dev/fuse) can be interrupted by a signal the .NET runtime delivers — GC / child reaping —
      // surfacing as a PosixException(EINTR) *before* the volume is up, i.e. "Interrupted system
      // call (4)". That is transient, so retry the mount while it has not yet come up rather than
      // failing outright; once mounted (registered) an EINTR is a real error and propagates.
      const int maxAttempts = 10;
      for (var attempt = 1; ; ++attempt) {
        try {
          // "-f": stay in the foreground so this process owns the lifecycle
          adapter.Mount(args);
          break;
        } catch (PosixException e) when (e.NativeErrorCode == PosixResult.EINTR && !registered && attempt < maxAttempts) {
          DriveBender.Logger($"[Warning]FUSE mount interrupted (EINTR) during setup — retrying ({attempt}/{maxAttempts - 1})");
          Thread.Sleep(150);
        }
      }
    } finally {
      // Housekeeping only, and bounded: a throttled member must not be able to hold the unmount
      // open. What is left behind is journalled and resumes on the next mount.
      if (!scheduler.Quiesce(BackgroundScheduler.UnmountBudget, fs.AbortBackgroundWork))
        DriveBender.Logger(
          $"[Warning]Unmounting with background work still pending after {BackgroundScheduler.UnmountBudget.TotalSeconds:F0}s "
          + "(a member's rate limit, or a slow disk) — it is journalled and resumes on the next mount.");
      fs.Unmount(); // clean unmount flushes everything (FR-CLEAN-UNMOUNT)
      onUnmounted?.Invoke();
    }

    return 0;
  }

  private static bool _IsMounted(string target) {
    var want = target.TrimEnd('/');
    try {
      foreach (var line in File.ReadLines("/proc/mounts")) {
        var parts = line.Split(' ');
        if (parts.Length >= 2 && parts[1].TrimEnd('/') == want)
          return true;
      }
    } catch (IOException) {
    }

    return false;
  }

  /// <summary>True if something is currently mounted at the target (any filesystem).</summary>
  public static bool IsMountpoint(string target) => OperatingSystem.IsLinux() && _IsMounted(target);

  /// <summary>
  /// A crashed or force-killed mount leaves the target as a DEAD FUSE mount: it still shows in
  /// /proc/mounts but every access fails ("Transport endpoint is not connected" / EACCES), so a
  /// fresh mount is refused with "fusermount3: failed to access mountpoint … Permission denied".
  /// Detect that specific state and lazily detach it so a remount can proceed; a live, healthy
  /// mount is left untouched. Returns true when a stale mount was cleared.
  /// </summary>
  public static bool TryClearStaleMount(string target) {
    if (!OperatingSystem.IsLinux() || !_IsMounted(target))
      return false;

    // a healthy mount answers a directory listing; a dead one throws
    try {
      using var walk = Directory.EnumerateFileSystemEntries(target).GetEnumerator();
      walk.MoveNext();
      return false; // accessible → not stale, leave it alone
    } catch {
      DriveBender.Logger($"[Warning]stale mount detected at '{target}' — detaching it before remounting");
      try { System.Diagnostics.Process.Start("fusermount3", ["-uz", target])?.WaitForExit(3000); } catch { }
      if (_IsMounted(target))
        try { System.Diagnostics.Process.Start("umount", ["-l", target])?.WaitForExit(3000); } catch { }
      return !_IsMounted(target);
    }
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
