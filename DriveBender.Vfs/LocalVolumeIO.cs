using System.Runtime.InteropServices;

namespace DivisonM.Vfs;

/// <summary>
/// <see cref="IVolumeIO"/> over a local (or UNC-mapped) directory tree — the backend for
/// drive-root, subfolder and UNC members. Honours the Drive Bender on-disk layout via
/// <see cref="PoolPaths"/> (SAFE-COMPAT).
/// </summary>
public sealed class LocalVolumeIO(Guid memberId, string displayName, string rootPath, string physicalVolumeId) : IVolumeIO {

  private static class NativeMethods {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = "GetDiskFreeSpaceEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
  }

  private readonly string _rootPath = System.IO.Path.GetFullPath(rootPath);

  public Guid MemberId { get; } = memberId;
  public string DisplayName { get; } = displayName;
  public string PhysicalVolumeId { get; } = physicalVolumeId;
  public string RootPath => this._rootPath;

  public bool IsOnline => Directory.Exists(this._rootPath);

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
    => this._Guard(() => (Stream)new FileStream(this._Resolve(relativePath, shadow), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

  public Stream OpenWrite(string relativePath, bool shadow, bool create) {
    return this._Guard(() => {
      var path = this._Resolve(relativePath, shadow);
      if (create)
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

    // FileOptions.WriteThrough so Flush is a durability barrier (DurableFlush cap)
      return (Stream)new FileStream(path, create ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.WriteThrough);
    });
  }

  public void Truncate(string relativePath, bool shadow, long length) => this._Guard(() => {
    using var stream = new FileStream(this._Resolve(relativePath, shadow), FileMode.Open, FileAccess.Write, FileShare.Read);
    stream.SetLength(length);
  });

  public void Delete(string relativePath, bool shadow) => this._Guard(() => {
    var path = this._Resolve(relativePath, shadow);
    var file = new FileInfo(path);
    if (!file.Exists)
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

    if ((file.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
      file.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);

    file.Delete();
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
    Directory.Move(fromPath, toPath);
  });

  public void AtomicReplace(string tempRelative, string finalRelative, bool shadow) => this._Guard(() => {
    var tempPath = this._Resolve(tempRelative, shadow);
    var finalPath = this._Resolve(finalRelative, shadow);
    if (!File.Exists(tempPath))
      throw new PoolFsException(PoolFsError.NotFound, $"Staged file not found: {tempRelative}");

    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(finalPath)!);
    if (File.Exists(finalPath))
      File.Replace(tempPath, finalPath, null, true);
    else
      File.Move(tempPath, finalPath);
  });

  public FileMeta? Stat(string relativePath, bool shadow) => this._Guard<FileMeta?>(() => {
    var path = this._Resolve(relativePath, shadow);
    var file = new FileInfo(path);
    if (file.Exists)
      return new FileMeta(file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, file.Attributes);

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

    foreach (var item in directory.EnumerateFileSystemInfos())
      yield return item switch {
        FileInfo f => new VolumeEntry(f.Name, false, f.Length, f.LastWriteTimeUtc),
        _ => new VolumeEntry(item.Name, true, 0, item.LastWriteTimeUtc),
      };
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
