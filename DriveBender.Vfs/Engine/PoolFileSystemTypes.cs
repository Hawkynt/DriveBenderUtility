namespace DivisonM.Vfs.Engine;

/// <summary>Opaque handle to an open node, stable for the mount session.</summary>
public readonly record struct NodeHandle(long Value) {
  public static readonly NodeHandle Invalid = new(0);
}

public enum NodeKind {
  File,
  Directory,
}

[Flags]
public enum CreateFlags {
  None = 0,
  Exclusive = 1,
  Truncate = 2,
}

[Flags]
public enum AccessMode {
  Read = 1,
  Write = 2,
  ReadWrite = Read | Write,
}

[Flags]
public enum ShareMode {
  None = 0,
  Read = 1,
  Write = 2,
  Delete = 4,
}

[Flags]
public enum RenameFlags {
  None = 0,
  ReplaceExisting = 1,
}

public enum WriteMode {
  Normal,
  Append,
}

public sealed record DirEntry(string Name, NodeKind Kind, long Length, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);

/// <summary>Pool aggregate for StatFs (FR-STAT): duplication-aware, reserve-adjusted, shared volumes de-duplicated.</summary>
public sealed record FsStatistics(long BytesTotal, long BytesFree, int BlockSize);

public sealed record FileMetaPatch(DateTime? CreationTimeUtc = null, DateTime? LastWriteTimeUtc = null, FileAttributes? Attributes = null);

public sealed record MountOptions(string Target, bool ReadOnly = false, string? VolumeLabel = null);

/// <summary>
/// The single VFS surface both platform adapters call (§6.2, CMP-VFS). Backend-neutral,
/// POSIX-ish; Win32-only concepts map in the adapter. Every failure is a
/// <see cref="PoolFsException"/> carrying a stable <see cref="PoolFsError"/> (FR-ERRNO).
/// </summary>
public interface IPoolFileSystem : IDisposable {
  void Mount(MountOptions options);
  void Unmount();

  FileMeta GetAttributes(string path);
  void SetAttributes(string path, FileMetaPatch patch);
  IReadOnlyList<DirEntry> ReadDirectory(string path);

  NodeHandle Create(string path, NodeKind kind, CreateFlags flags);
  NodeHandle Open(string path, AccessMode mode, ShareMode share);
  void Rename(string from, string to, RenameFlags flags);
  void Unlink(string path);
  void MakeDir(string path);
  void RemoveDir(string path);

  int Read(NodeHandle handle, Span<byte> buffer, long offset);
  int Write(NodeHandle handle, ReadOnlySpan<byte> data, long offset, WriteMode mode);
  void SetLength(NodeHandle handle, long length);
  void Flush(NodeHandle handle);
  void Close(NodeHandle handle);

  FsStatistics StatFs();
}
