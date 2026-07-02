using System.Security.AccessControl;
using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DokanNet;
using DokanNet.Logging;
using FileAccess = DokanNet.FileAccess;

namespace DivisonM.Mount.Windows;

/// <summary>
/// The Dokan platform adapter (§4.1, the PRD's accepted WinFsp alternative): a thin
/// translation between dokan-dotnet callbacks and <see cref="IPoolFileSystem"/>, so
/// users with the Dokan driver (LGPL) need not install WinFsp. No pool logic lives here
/// (NFR-PORT).
/// </summary>
public sealed class DokanAdapter(IPoolFileSystem pool, string volumeLabel) : IDokanOperations {

  private static string _ToPoolPath(string fileName) => fileName.Replace('\\', '/').Trim('/');

  private static NtStatus _Translate(PoolFsException e) => e.Error switch {
    PoolFsError.NotFound => DokanResult.FileNotFound,
    PoolFsError.AccessDenied => DokanResult.AccessDenied,
    PoolFsError.Exists => DokanResult.AlreadyExists,
    PoolFsError.NotEmpty => DokanResult.DirectoryNotEmpty,
    PoolFsError.NoSpace => DokanResult.DiskFull,
    PoolFsError.StaleHandle => DokanResult.InvalidHandle,
    PoolFsError.NotSupported => DokanResult.NotImplemented,
    PoolFsError.InvalidArgument => DokanResult.InvalidParameter,
    PoolFsError.NotADirectory => DokanResult.NotADirectory,
    PoolFsError.IsADirectory => DokanResult.AccessDenied,
    _ => DokanResult.Error,
  };

  private static FileInformation _ToInformation(string name, FileMeta meta) => new() {
    FileName = name,
    Attributes = meta.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
    Length = meta.IsDirectory ? 0 : meta.Length,
    CreationTime = meta.CreationTimeUtc == DateTime.MinValue ? null : meta.CreationTimeUtc.ToLocalTime(),
    LastWriteTime = meta.LastWriteTimeUtc == DateTime.MinValue ? null : meta.LastWriteTimeUtc.ToLocalTime(),
    LastAccessTime = meta.LastWriteTimeUtc == DateTime.MinValue ? null : meta.LastWriteTimeUtc.ToLocalTime(),
  };

  public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info) {
    var path = _ToPoolPath(fileName);
    try {
      FileMeta? existing = null;
      try {
        existing = pool.GetAttributes(path);
      } catch (PoolFsException e) when (e.Error == PoolFsError.NotFound) {
      }

      if (info.IsDirectory) {
        switch (mode) {
          case FileMode.Open:
            if (existing == null)
              return DokanResult.PathNotFound;

            return existing.Value.IsDirectory ? DokanResult.Success : DokanResult.NotADirectory;

          case FileMode.CreateNew:
            if (existing != null)
              return DokanResult.FileExists;

            pool.MakeDir(path);
            return DokanResult.Success;

          default:
            return DokanResult.Success;
        }
      }

      if (existing is { IsDirectory: true }) {
        info.IsDirectory = true;
        return mode is FileMode.Open or FileMode.OpenOrCreate ? DokanResult.Success : DokanResult.AccessDenied;
      }

      var wantsWrite = (access & (FileAccess.WriteData | FileAccess.AppendData | FileAccess.GenericWrite | FileAccess.Delete)) != 0;
      switch (mode) {
        case FileMode.Open:
          if (existing == null)
            return DokanResult.FileNotFound;

          if ((access & (FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData | FileAccess.GenericRead | FileAccess.GenericWrite | FileAccess.Execute)) == 0)
            return DokanResult.Success; // attribute-only open — no data handle needed

          info.Context = pool.Open(path, wantsWrite ? AccessMode.ReadWrite : AccessMode.Read, ShareMode.Read | ShareMode.Write);
          return DokanResult.Success;

        case FileMode.CreateNew:
          if (existing != null)
            return DokanResult.FileExists;

          info.Context = pool.Create(path, NodeKind.File, CreateFlags.Exclusive);
          return DokanResult.Success;

        case FileMode.Create:
        case FileMode.Truncate:
          info.Context = existing == null && mode == FileMode.Create
            ? pool.Create(path, NodeKind.File, CreateFlags.None)
            : pool.Create(path, NodeKind.File, CreateFlags.Truncate);
          return existing != null ? DokanResult.AlreadyExists : DokanResult.Success;

        case FileMode.OpenOrCreate:
        case FileMode.Append:
          info.Context = existing == null
            ? pool.Create(path, NodeKind.File, CreateFlags.None)
            : pool.Open(path, wantsWrite ? AccessMode.ReadWrite : AccessMode.Read, ShareMode.Read | ShareMode.Write);
          return existing != null ? DokanResult.AlreadyExists : DokanResult.Success;

        default:
          return DokanResult.InvalidParameter;
      }
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  private NodeHandle _HandleOf(IDokanFileInfo info) => info.Context is NodeHandle handle ? handle : NodeHandle.Invalid;

  public void Cleanup(string fileName, IDokanFileInfo info) {
    var path = _ToPoolPath(fileName);
    try {
      var handle = this._HandleOf(info);
      if (handle != NodeHandle.Invalid) {
        pool.Close(handle);
        info.Context = null;
      }

      // the Windows delete protocol: DeleteFile/DeleteDirectory only validated; the
      // deletion happens here once the last handle goes away with delete pending
      if (info.DeletePending) {
        if (info.IsDirectory)
          pool.RemoveDir(path);
        else
          pool.Unlink(path);
      }
    } catch (PoolFsException e) {
      DriveBender.Logger($"[Warning]Cleanup of '{path}' failed: {e.Message}");
    }
  }

  public void CloseFile(string fileName, IDokanFileInfo info) {
    var handle = this._HandleOf(info);
    if (handle == NodeHandle.Invalid)
      return;

    try {
      pool.Close(handle);
    } catch (PoolFsException) {
      // already closed in Cleanup
    }

    info.Context = null;
  }

  public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info) {
    bytesRead = 0;
    try {
      var handle = this._HandleOf(info);
      var transient = handle == NodeHandle.Invalid;
      if (transient)
        handle = pool.Open(_ToPoolPath(fileName), AccessMode.Read, ShareMode.Read | ShareMode.Write);

      try {
        bytesRead = pool.Read(handle, buffer, offset);
      } finally {
        if (transient)
          pool.Close(handle);
      }

      return DokanResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info) {
    bytesWritten = 0;
    try {
      var handle = this._HandleOf(info);
      var transient = handle == NodeHandle.Invalid;
      if (transient)
        handle = pool.Open(_ToPoolPath(fileName), AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);

      try {
        bytesWritten = pool.Write(handle, buffer, offset < 0 ? 0 : offset, offset < 0 ? WriteMode.Append : WriteMode.Normal);
      } finally {
        if (transient)
          pool.Close(handle);
      }

      return DokanResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) {
    try {
      var handle = this._HandleOf(info);
      if (handle != NodeHandle.Invalid)
        pool.Flush(handle);

      return DokanResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info) {
    fileInfo = default;
    try {
      var path = _ToPoolPath(fileName);
      fileInfo = _ToInformation(Path.GetFileName(fileName.TrimEnd('\\')), pool.GetAttributes(path));
      return DokanResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    => this.FindFilesWithPattern(fileName, "*", out files, info);

  public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info) {
    files = [];
    try {
      var entries = pool.ReadDirectory(_ToPoolPath(fileName));
      files = entries
        .Where(entry => DokanHelper.DokanIsNameInExpression(searchPattern, entry.Name, true))
        .Select(entry => _ToInformation(entry.Name, new(entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Kind == NodeKind.Directory ? FileAttributes.Directory : FileAttributes.Normal)))
        .ToList();
      return DokanResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info) => DokanResult.Success;

  public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info) {
    try {
      pool.SetAttributes(_ToPoolPath(fileName), new(creationTime?.ToUniversalTime(), lastWriteTime?.ToUniversalTime()));
      return DokanResult.Success;
    } catch (PoolFsException e) {
      return e.Error == PoolFsError.NotSupported ? DokanResult.Success : _Translate(e);
    }
  }

  public NtStatus DeleteFile(string fileName, IDokanFileInfo info) {
    try {
      var meta = pool.GetAttributes(_ToPoolPath(fileName));
      return meta.IsDirectory ? DokanResult.AccessDenied : DokanResult.Success; // validation only; Cleanup deletes
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info) {
    try {
      return pool.ReadDirectory(_ToPoolPath(fileName)).Count > 0
        ? DokanResult.DirectoryNotEmpty
        : DokanResult.Success; // validation only; Cleanup deletes
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info) {
    try {
      var handle = this._HandleOf(info);
      if (handle != NodeHandle.Invalid) {
        pool.Close(handle);
        info.Context = null;
      }

      pool.Rename(_ToPoolPath(oldName), _ToPoolPath(newName), replace ? RenameFlags.ReplaceExisting : RenameFlags.None);
      return DokanResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info) {
    try {
      var handle = this._HandleOf(info);
      var transient = handle == NodeHandle.Invalid;
      if (transient)
        handle = pool.Open(_ToPoolPath(fileName), AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);

      try {
        pool.SetLength(handle, length);
      } finally {
        if (transient)
          pool.Close(handle);
      }

      return DokanResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) => DokanResult.Success;

  public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

  public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

  public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info) {
    freeBytesAvailable = 0;
    totalNumberOfBytes = 0;
    totalNumberOfFreeBytes = 0;
    try {
      var stats = pool.StatFs();
      freeBytesAvailable = stats.BytesFree;
      totalNumberOfBytes = stats.BytesTotal;
      totalNumberOfFreeBytes = stats.BytesFree;
      return DokanResult.Success;
    } catch (PoolFsException e) {
      return _Translate(e);
    }
  }

  public NtStatus GetVolumeInformation(out string volumeLabelOut, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info) {
    volumeLabelOut = volumeLabel;
    features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
    fileSystemName = "DriveBender";
    maximumComponentLength = 255;
    return DokanResult.Success;
  }

  public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info) {
    security = null;
    return DokanResult.NotImplemented; // ACLs are passthrough-best-effort (FR-PERMS); Dokan falls back to defaults
  }

  public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    => DokanResult.NotImplemented;

  public NtStatus Mounted(string mountPoint, IDokanFileInfo info) {
    DriveBender.Logger($"Mounted pool as '{mountPoint}' via Dokan");
    return DokanResult.Success;
  }

  public NtStatus Unmounted(IDokanFileInfo info) => DokanResult.Success;

  public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info) {
    streams = [];
    return DokanResult.NotImplemented;
  }

}

/// <summary>Hosts a pool behind the Dokan driver — the no-WinFsp-required Windows path.</summary>
public sealed class DokanMountHost : IDisposable {

  private Dokan? _dokan;
  private DokanInstance? _instance;

  public static bool IsDokanAvailable() {
    try {
      using var dokan = new Dokan(new NullLogger());
      return dokan.Version > 0;
    } catch (DllNotFoundException) {
      return false;
    } catch (DokanException) {
      return false;
    }
  }

  public void Mount(IPoolFileSystem pool, string target, string volumeLabel, bool readOnly) {
    pool.Mount(new(target, readOnly, volumeLabel));

    var adapter = new DokanAdapter(pool, volumeLabel);
    this._dokan = new(new NullLogger());
    var builder = new DokanInstanceBuilder(this._dokan).ConfigureOptions(options => {
      options.MountPoint = target;
      options.Options = readOnly ? DokanOptions.WriteProtection : DokanOptions.FixedDrive;
    });

    this._instance = builder.Build(adapter);
    DriveBender.Logger($"Mounting pool at '{target}' via Dokan");
  }

  public void WaitUntilUnmounted() => this._instance?.WaitForFileSystemClosedAsync(uint.MaxValue).GetAwaiter().GetResult();

  public void Unmount() {
    this._instance?.Dispose();
    this._instance = null;
    this._dokan?.Dispose();
    this._dokan = null;
  }

  public void Dispose() => this.Unmount();

}
