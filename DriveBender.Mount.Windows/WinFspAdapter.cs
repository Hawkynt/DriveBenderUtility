using System.Runtime.InteropServices;
using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using Fsp;
using FspFileInfo = Fsp.Interop.FileInfo;
using FspVolumeInfo = Fsp.Interop.VolumeInfo;

namespace DivisonM.Mount.Windows;

/// <summary>
/// The WinFsp platform adapter (§4.1): a thin translation between WinFsp callbacks and
/// <see cref="IPoolFileSystem"/>. No pool logic lives here (NFR-PORT) — errors map from
/// <see cref="PoolFsError"/> to NTSTATUS, paths and attributes convert, nothing more.
/// </summary>
public sealed class WinFspAdapter(IPoolFileSystem pool, string volumeLabel) : FileSystemBase {

  private sealed class FileDescriptor {
    public NodeHandle Handle = NodeHandle.Invalid;
    public required string Path;
    public bool IsDirectory;
  }

  private const uint FILE_WRITE_DATA = 0x0002;
  private const uint FILE_APPEND_DATA = 0x0004;

  private static string _ToPoolPath(string fileName) => fileName.Replace('\\', '/').Trim('/');

  private const int STATUS_DEVICE_NOT_READY = unchecked((int)0xC00000A3);

  private static int _Translate(PoolFsException e) => e.Error switch {
    PoolFsError.NotFound => STATUS_OBJECT_NAME_NOT_FOUND,
    PoolFsError.AccessDenied => STATUS_ACCESS_DENIED,
    // a member offline / ack quorum not met is a "device not ready", not an anonymous I/O error
    PoolFsError.Offline => STATUS_DEVICE_NOT_READY,
    PoolFsError.Exists => STATUS_OBJECT_NAME_COLLISION,
    PoolFsError.NotEmpty => STATUS_DIRECTORY_NOT_EMPTY,
    PoolFsError.NoSpace => STATUS_DISK_FULL,
    PoolFsError.StaleHandle => STATUS_INVALID_HANDLE,
    PoolFsError.NotSupported => STATUS_INVALID_DEVICE_REQUEST,
    PoolFsError.InvalidArgument => STATUS_INVALID_PARAMETER,
    PoolFsError.IsADirectory => STATUS_FILE_IS_A_DIRECTORY,
    PoolFsError.NotADirectory => STATUS_NOT_A_DIRECTORY,
    _ => STATUS_UNEXPECTED_IO_ERROR,
  };

  private static void _Fill(FileMeta meta, out FspFileInfo info) {
    info = default;
    info.FileAttributes = meta.IsDirectory ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal;
    info.FileSize = (ulong)meta.Length;
    info.AllocationSize = (info.FileSize + 4095) / 4096 * 4096;
    var created = meta.CreationTimeUtc == DateTime.MinValue ? 0UL : (ulong)meta.CreationTimeUtc.ToFileTimeUtc();
    var written = meta.LastWriteTimeUtc == DateTime.MinValue ? 0UL : (ulong)meta.LastWriteTimeUtc.ToFileTimeUtc();
    info.CreationTime = created;
    info.LastAccessTime = written;
    info.LastWriteTime = written;
    info.ChangeTime = written;
  }

  public override int GetVolumeInfo(out FspVolumeInfo volumeInfo) {
    volumeInfo = default;
    try {
      var stats = pool.StatFs();
      volumeInfo.TotalSize = (ulong)stats.BytesTotal;
      volumeInfo.FreeSize = (ulong)stats.BytesFree;
      volumeInfo.SetVolumeLabel(volumeLabel);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor) {
    fileAttributes = 0;
    try {
      var meta = pool.GetAttributes(_ToPoolPath(fileName));
      fileAttributes = meta.IsDirectory ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal;
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor, ulong allocationSize, out object? fileNode, out object? fileDesc, out FspFileInfo fileInfo, out string? normalizedName) {
    fileNode = null;
    fileDesc = null;
    fileInfo = default;
    normalizedName = null;
    var path = _ToPoolPath(fileName);
    try {
      var isDirectory = (createOptions & FILE_DIRECTORY_FILE) != 0;
      var descriptor = new FileDescriptor { Path = path, IsDirectory = isDirectory };
      if (isDirectory)
        pool.MakeDir(path);
      else
        descriptor.Handle = pool.Create(path, NodeKind.File, CreateFlags.Exclusive);

      fileDesc = descriptor;
      normalizedName = fileName;
      _Fill(pool.GetAttributes(path), out fileInfo);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int Open(string fileName, uint createOptions, uint grantedAccess, out object? fileNode, out object? fileDesc, out FspFileInfo fileInfo, out string? normalizedName) {
    fileNode = null;
    fileDesc = null;
    fileInfo = default;
    normalizedName = null;
    var path = _ToPoolPath(fileName);
    try {
      var meta = pool.GetAttributes(path);
      var descriptor = new FileDescriptor { Path = path, IsDirectory = meta.IsDirectory };
      if (!meta.IsDirectory) {
        var wantsWrite = (grantedAccess & (FILE_WRITE_DATA | FILE_APPEND_DATA)) != 0;
        descriptor.Handle = pool.Open(path, wantsWrite ? AccessMode.ReadWrite : AccessMode.Read, ShareMode.Read | ShareMode.Write);
      }

      fileDesc = descriptor;
      normalizedName = fileName;
      _Fill(meta, out fileInfo);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred) {
    bytesTransferred = 0;
    var descriptor = (FileDescriptor)fileDesc;
    try {
      var scratch = new byte[length];
      var read = pool.Read(descriptor.Handle, scratch, (long)offset);
      if (read == 0)
        return STATUS_END_OF_FILE;

      Marshal.Copy(scratch, 0, buffer, read);
      bytesTransferred = (uint)read;
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FspFileInfo fileInfo) {
    bytesTransferred = 0;
    fileInfo = default;
    var descriptor = (FileDescriptor)fileDesc;
    try {
      if (constrainedIo) {
        var currentLength = pool.GetAttributes(descriptor.Path).Length;
        if ((long)offset >= currentLength) {
          _Fill(pool.GetAttributes(descriptor.Path), out fileInfo);
          return STATUS_SUCCESS;
        }

        length = (uint)Math.Min(length, currentLength - (long)offset);
      }

      var scratch = new byte[length];
      Marshal.Copy(buffer, scratch, 0, (int)length);
      var written = pool.Write(descriptor.Handle, scratch, writeToEndOfFile ? 0 : (long)offset, writeToEndOfFile ? WriteMode.Append : WriteMode.Normal);
      bytesTransferred = (uint)written;
      _Fill(pool.GetAttributes(descriptor.Path), out fileInfo);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int Flush(object fileNode, object fileDesc, out FspFileInfo fileInfo) {
    fileInfo = default;
    if (fileDesc is not FileDescriptor descriptor)
      return STATUS_SUCCESS; // volume flush

    try {
      if (!descriptor.IsDirectory)
        pool.Flush(descriptor.Handle);

      _Fill(pool.GetAttributes(descriptor.Path), out fileInfo);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int GetFileInfo(object fileNode, object fileDesc, out FspFileInfo fileInfo) {
    fileInfo = default;
    var descriptor = (FileDescriptor)fileDesc;
    try {
      _Fill(pool.GetAttributes(descriptor.Path), out fileInfo);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FspFileInfo fileInfo) {
    fileInfo = default;
    var descriptor = (FileDescriptor)fileDesc;
    try {
      pool.SetAttributes(descriptor.Path, new(
        creationTime != 0 ? DateTime.FromFileTimeUtc((long)creationTime) : null,
        lastWriteTime != 0 ? DateTime.FromFileTimeUtc((long)lastWriteTime) : null));
      _Fill(pool.GetAttributes(descriptor.Path), out fileInfo);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FspFileInfo fileInfo) {
    fileInfo = default;
    var descriptor = (FileDescriptor)fileDesc;
    try {
      if (!setAllocationSize)
        pool.SetLength(descriptor.Handle, (long)newSize);

      _Fill(pool.GetAttributes(descriptor.Path), out fileInfo);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int CanDelete(object fileNode, object fileDesc, string fileName) {
    var descriptor = (FileDescriptor)fileDesc;
    try {
      if (descriptor.IsDirectory && pool.ReadDirectory(descriptor.Path).Count > 0)
        return STATUS_DIRECTORY_NOT_EMPTY;

      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) {
    var descriptor = (FileDescriptor)fileDesc;
    try {
      pool.Rename(_ToPoolPath(fileName), _ToPoolPath(newFileName), replaceIfExists ? RenameFlags.ReplaceExisting : RenameFlags.None);
      // WinFsp keeps the handle open across the rename and immediately calls GetFileInfo/Cleanup/Close
      // on it — repoint the descriptor to the new path, or those hit the old (gone) name and Explorer
      // reports "the item has moved".
      descriptor.Path = _ToPoolPath(newFileName);
      return STATUS_SUCCESS;
    } catch (PoolFsException e) {
      return _Translate(e);
    } catch (Exception e) {
      // a driver callback must NEVER let an exception escape — that kills the whole mount process
      DriveBender.Logger($"[Warning]unexpected error in a filesystem callback: {e}");
      return STATUS_UNEXPECTED_IO_ERROR;
    }
  }

  public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags) {
    var descriptor = (FileDescriptor)fileDesc;
    if ((flags & CleanupDelete) == 0)
      return;

    try {
      if (descriptor.IsDirectory)
        pool.RemoveDir(descriptor.Path);
      else {
        if (descriptor.Handle != NodeHandle.Invalid) {
          pool.Close(descriptor.Handle);
          descriptor.Handle = NodeHandle.Invalid;
        }

        pool.Unlink(descriptor.Path);
      }
    } catch (PoolFsException e) {
      DriveBender.Logger($"[Warning]Cleanup-delete of '{descriptor.Path}' failed: {e.Message}");
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]unexpected error in Cleanup of '{descriptor.Path}': {e}");
    }
  }

  public override void Close(object fileNode, object fileDesc) {
    var descriptor = (FileDescriptor)fileDesc;
    if (descriptor.Handle == NodeHandle.Invalid)
      return;

    try {
      pool.Close(descriptor.Handle);
    } catch (PoolFsException) {
      // already gone (e.g. cleanup-delete)
    }
  }

  public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string? marker, ref object? context, out string? fileName, out FspFileInfo fileInfo) {
    fileName = null;
    fileInfo = default;
    var descriptor = (FileDescriptor)fileDesc;

    if (context is not IEnumerator<DirEntry> enumerator) {
      IEnumerable<DirEntry> entries;
      try {
        entries = pool.ReadDirectory(descriptor.Path);
      } catch (PoolFsException) {
        return false;
      }

      if (marker != null)
        entries = entries.SkipWhile(e => !e.Name.Equals(marker, StringComparison.OrdinalIgnoreCase)).Skip(1);

      context = enumerator = entries.GetEnumerator();
    }

    if (!enumerator.MoveNext())
      return false;

    var entry = enumerator.Current;
    fileName = entry.Name;
    _Fill(new(entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Kind == NodeKind.Directory ? FileAttributes.Directory : FileAttributes.Normal), out fileInfo);
    return true;
  }

}

/// <summary>Hosts a pool behind WinFsp at a drive letter or an empty NTFS directory (FR-MOUNT-WIN-GUI/CLI).</summary>
public sealed class WinFspMountHost : IDisposable {

  private FileSystemHost? _host;

  /// <summary>True when the WinFsp runtime is installed on this machine.</summary>
  /// <remarks>
  /// Prefers the authoritative managed probe, but falls back to the registry/DLL the installer
  /// writes. winfsp.net resolves the native DLL in a static initializer, so a long-lived process
  /// that probed <em>before</em> WinFsp was installed poisons that type for its whole lifetime —
  /// the on-disk fallback lets the daemon see a just-installed driver without a restart, while a
  /// freshly-spawned mount child still loads the DLL normally.
  /// </remarks>
  public static bool IsWinFspAvailable() {
    try {
      if (FileSystemHost.Version() != null)
        return true;
    } catch (DllNotFoundException) {
      // fall through to the on-disk probe
    } catch (TypeInitializationException) {
      // fall through to the on-disk probe
    }

    return _IsWinFspInstalledOnDisk();
  }

  private static bool _IsWinFspInstalledOnDisk() {
    try {
      var dllName = Environment.Is64BitOperatingSystem ? "winfsp-x64.dll" : "winfsp-x86.dll";
      foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry32, Microsoft.Win32.RegistryView.Registry64 }) {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\WinFsp");
        if (key?.GetValue("InstallDir") is string dir && dir.Length > 0 && File.Exists(Path.Combine(dir, "bin", dllName)))
          return true;
      }

      // registry aside, the installer's default location is the reliable last resort
      var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
      return programFilesX86.Length > 0 && File.Exists(Path.Combine(programFilesX86, "WinFsp", "bin", dllName));
    } catch (Exception) {
      return false;
    }
  }

  public void Mount(IPoolFileSystem pool, string target, string volumeLabel, bool readOnly) {
    pool.Mount(new(target, readOnly, volumeLabel));

    var adapter = new WinFspAdapter(pool, volumeLabel);
    this._host = new(adapter) {
      SectorSize = 4096,
      SectorsPerAllocationUnit = 1,
      MaxComponentLength = 255,
      FileInfoTimeout = 1000,
      CaseSensitiveSearch = false,
      CasePreservedNames = true,
      UnicodeOnDisk = true,
      PersistentAcls = false,
      PostCleanupWhenModifiedOnly = true,
      VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc(),
      VolumeSerialNumber = 0,
    };

    var status = this._host.Mount(target);
    if (status < 0) {
      this._host = null;
      pool.Unmount();
      throw new PoolFsException(PoolFsError.IoError, $"WinFsp mount at '{target}' failed with NTSTATUS 0x{status:X8}");
    }

    DriveBender.Logger($"Mounted pool as '{target}' via WinFsp");
  }

  public void Unmount() {
    this._host?.Unmount();
    this._host = null;
  }

  public void Dispose() => this.Unmount();

}
