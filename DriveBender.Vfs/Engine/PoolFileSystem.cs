using DivisonM.Vfs.Caching;

namespace DivisonM.Vfs.Engine;

/// <summary>One member as the engine sees it: its I/O backend plus manifest facts.</summary>
public sealed record EngineMember(IVolumeIO Io, MemberRole Role = MemberRole.Capacity, long ReserveBytes = 0);

/// <summary>
/// The VFS engine (CMP-VFS) over a set of pool members: presents the merged logical
/// namespace (FR-DIR), hides on-disk sidecars (FR-HIDE), serves block-aligned cached
/// reads with read-ahead (FR-RA) and mirror block routing (FR-MIRROR), and reports
/// duplication-aware pool statistics (FR-STAT).
///
/// Milestone M1: the read side. Mutating operations arrive with the journalled write
/// path (M2) and until then fail with <see cref="PoolFsError.NotSupported"/>.
/// </summary>
public sealed class PoolFileSystem : IPoolFileSystem {

  private readonly Guid _poolId;
  private readonly IReadOnlyList<EngineMember> _members;
  private readonly CacheInstance _cache;
  private readonly PoolConfig _config;
  private readonly PlacementResolver _placement;
  private readonly HandleTable _handles = new();
  private readonly long _readAheadMin;
  private readonly long _readAheadMax;
  private readonly bool _readAheadEnabled;
  private readonly bool _readAheadAdaptive;
  private readonly long _mirrorSplitThreshold;
  private MountOptions? _mountOptions;

  public PoolFileSystem(Guid poolId, IReadOnlyList<EngineMember> members, CacheInstance cache, PoolConfig effectiveConfig) {
    this._poolId = poolId;
    this._members = members;
    this._cache = cache;
    this._config = effectiveConfig;
    this._placement = new(
      poolId,
      [.. members.Select(m => m.Io)],
      cache.Metadata,
      effectiveConfig,
      members.ToDictionary(m => m.Io.MemberId, m => m.Role));

    var readAhead = effectiveConfig.ReadAhead;
    this._readAheadEnabled = readAhead?.Enabled ?? true;
    this._readAheadMin = SizeSpec.ParseBytes(readAhead?.MinWindow ?? "1MiB");
    this._readAheadMax = SizeSpec.ParseBytes(readAhead?.MaxWindow ?? "8MiB");
    this._readAheadAdaptive = readAhead?.Adaptive ?? true;
    this._mirrorSplitThreshold = SizeSpec.ParseBytes(effectiveConfig.Io?.MirrorReadSplitThreshold ?? "8MiB");
  }

  public PlacementResolver Placement => this._placement;
  public bool IsMounted => this._mountOptions != null;
  public bool IsReadOnly => this._mountOptions?.ReadOnly ?? false;

  private IEnumerable<IVolumeIO> _Online => this._members.Where(m => m.Io.IsOnline).Select(m => m.Io);

  public void Mount(MountOptions options) {
    if (this._mountOptions != null)
      throw new PoolFsException(PoolFsError.Exists, "Pool is already mounted");

    if (!this._Online.Any())
      throw new PoolFsException(PoolFsError.Offline, "No pool member is online — refusing to mount");

    this._mountOptions = options;
  }

  public void Unmount() => this._mountOptions = null;

  public void Dispose() => this.Unmount();

  private void _RequireMounted() {
    if (this._mountOptions == null)
      throw new PoolFsException(PoolFsError.InvalidArgument, "Pool is not mounted");
  }

  private PoolFsException _WriteNotSupportedYet()
    => this.IsReadOnly
      ? new(PoolFsError.AccessDenied, "Pool is mounted read-only")
      : new PoolFsException(PoolFsError.NotSupported, "The journalled write path lands with milestone M2");

  #region metadata

  public FileMeta GetAttributes(string path) {
    this._RequireMounted();
    var normalized = PoolPaths.Normalize(path);
    var key = new MetadataKey(this._poolId, normalized, MetadataKind.Stat);
    if (this._cache.Metadata.TryGet<FileMeta>(key, out var cached))
      return cached;

    var meta = this._StatUncached(normalized)
               ?? throw new PoolFsException(PoolFsError.NotFound, $"Path not found: {path}");

    this._cache.Metadata.Put(key, meta);
    return meta;
  }

  private FileMeta? _StatUncached(string normalized) {
    if (normalized.Length == 0)
      return new(0, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory);

    // logical size = one copy's size, never the sum of copies (FR-STAT)
    var copies = this._placement.ResolveCopies(normalized);
    if (copies.Count > 0)
      return copies[0].Volume.Stat(normalized, copies[0].Shadow);

    foreach (var member in this._Online)
      if (member.FolderExists(normalized, false))
        return new(0, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory);

    return null;
  }

  public void SetAttributes(string path, FileMetaPatch patch) {
    this._RequireMounted();
    throw this._WriteNotSupportedYet();
  }

  public IReadOnlyList<DirEntry> ReadDirectory(string path) {
    this._RequireMounted();
    var normalized = PoolPaths.Normalize(path);
    var key = new MetadataKey(this._poolId, normalized, MetadataKind.DirectoryListing);
    if (this._cache.Metadata.TryGet<IReadOnlyList<DirEntry>>(key, out var cached))
      return cached;

    var entries = new Dictionary<string, DirEntry>(StringComparer.OrdinalIgnoreCase);
    var folderSeen = normalized.Length == 0;

    foreach (var member in this._Online) {
      if (!member.FolderExists(normalized, false))
        continue;

      folderSeen = true;
      foreach (var entry in member.List(normalized, false)) {
        if (PoolPaths.IsHiddenName(entry.Name) || entries.ContainsKey(entry.Name))
          continue;

        entries.Add(entry.Name, new(
          entry.Name,
          entry.IsDirectory ? NodeKind.Directory : NodeKind.File,
          entry.Length,
          entry.LastWriteTimeUtc,
          entry.LastWriteTimeUtc));
      }
    }

    // shadow-only files: a primary may be lost while its shadow copy survives
    foreach (var member in this._Online) {
      if (!member.FolderExists(normalized, true))
        continue;

      folderSeen = true;
      foreach (var entry in member.List(normalized, true)) {
        if (entry.IsDirectory || PoolPaths.IsHiddenName(entry.Name) || entries.ContainsKey(entry.Name))
          continue;

        entries.Add(entry.Name, new(entry.Name, NodeKind.File, entry.Length, entry.LastWriteTimeUtc, entry.LastWriteTimeUtc));
      }
    }

    if (!folderSeen)
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {path}");

    IReadOnlyList<DirEntry> result = [.. entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    this._cache.Metadata.Put(key, result);
    return result;
  }

  #endregion

  #region namespace (M2)

  public NodeHandle Create(string path, NodeKind kind, CreateFlags flags) {
    this._RequireMounted();
    throw this._WriteNotSupportedYet();
  }

  public void Rename(string from, string to, RenameFlags flags) {
    this._RequireMounted();
    throw this._WriteNotSupportedYet();
  }

  public void Unlink(string path) {
    this._RequireMounted();
    throw this._WriteNotSupportedYet();
  }

  public void MakeDir(string path) {
    this._RequireMounted();
    throw this._WriteNotSupportedYet();
  }

  public void RemoveDir(string path) {
    this._RequireMounted();
    throw this._WriteNotSupportedYet();
  }

  #endregion

  #region data

  public NodeHandle Open(string path, AccessMode mode, ShareMode share) {
    this._RequireMounted();
    if ((mode & AccessMode.Write) != 0)
      throw this._WriteNotSupportedYet();

    var normalized = PoolPaths.Normalize(path);
    if (this._placement.ResolveCopies(normalized).Count == 0)
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {path}");

    return this._handles.Open(normalized, mode).Handle;
  }

  public int Read(NodeHandle handle, Span<byte> buffer, long offset) {
    this._RequireMounted();
    var open = this._handles.Get(handle);
    if (offset < 0)
      throw new PoolFsException(PoolFsError.InvalidArgument, "Negative offset");

    open.File.Lock.EnterReadLock();
    try {
      var path = open.File.Path;
      var copies = this._placement.ResolveCopies(path);
      if (copies.Count == 0)
        throw new PoolFsException(PoolFsError.NotFound, $"File vanished: {path}");

      var length = copies[0].Volume.Stat(path, copies[0].Shadow)?.Length ?? 0;
      if (offset >= length)
        return 0; // reads past EOF return 0 bytes (FR-READ)

      var count = (int)Math.Min(buffer.Length, length - offset);
      this._ReadRange(path, copies, buffer[..count], offset, count >= this._mirrorSplitThreshold);

      if (this._readAheadEnabled) {
        ReadAheadState? state;
        lock (open.File.ReadAhead) {
          if (!open.File.ReadAhead.TryGetValue(handle.Value, out state))
            open.File.ReadAhead.Add(handle.Value, state = new(this._readAheadMin, this._readAheadMax, this._readAheadAdaptive));
        }

        long prefetchBytes;
        lock (state)
          prefetchBytes = state.OnRead(offset, count);

        if (prefetchBytes > 0)
          this._Prefetch(path, copies, offset + count, prefetchBytes, length);
      }

      return count;
    } finally {
      open.File.Lock.ExitReadLock();
    }
  }

  private void _ReadRange(string path, IReadOnlyList<PhysicalCopy> copies, Span<byte> buffer, long offset, bool mirrorSplit) {
    var blockSize = this._cache.Pages.BlockSize;
    var written = 0;
    while (written < buffer.Length) {
      var absolute = offset + written;
      var blockIndex = absolute / blockSize;
      var blockOffset = (int)(absolute % blockSize);
      var block = this._LoadBlock(path, copies, blockIndex, mirrorSplit);
      var available = Math.Min(buffer.Length - written, block.Length - blockOffset);
      if (available <= 0)
        throw new PoolFsException(PoolFsError.IoError, $"Short read at block {blockIndex} of '{path}'");

      block.AsSpan(blockOffset, available).CopyTo(buffer[written..]);
      written += available;
    }
  }

  private byte[] _LoadBlock(string path, IReadOnlyList<PhysicalCopy> copies, long blockIndex, bool mirrorSplit) {
    var key = new PageKey(this._poolId, path, blockIndex);
    if (this._cache.Pages.TryGet(key, out var cached))
      return cached;

    // mirror block routing (FR-MIRROR): large reads alternate blocks across copies
    var copy = mirrorSplit && copies.Count > 1
      ? copies[(int)(blockIndex % copies.Count)]
      : copies[0];

    var blockSize = this._cache.Pages.BlockSize;
    using var stream = copy.Volume.OpenRead(path, copy.Shadow);
    stream.Seek(blockIndex * blockSize, SeekOrigin.Begin);
    var block = new byte[blockSize];
    var total = 0;
    while (total < blockSize) {
      var read = stream.Read(block, total, blockSize - total);
      if (read == 0)
        break;

      total += read;
    }

    if (total < blockSize)
      Array.Resize(ref block, total);

    this._cache.Pages.Put(key, block);
    return block;
  }

  private void _Prefetch(string path, IReadOnlyList<PhysicalCopy> copies, long fromOffset, long windowBytes, long fileLength) {
    var blockSize = this._cache.Pages.BlockSize;
    var lastByte = Math.Min(fileLength, fromOffset + windowBytes) - 1; // never past EOF (FR-RA)
    for (var blockIndex = fromOffset / blockSize; blockIndex <= lastByte / blockSize; ++blockIndex) {
      var key = new PageKey(this._poolId, path, blockIndex);
      if (this._cache.Pages.TryGet(key, out _))
        continue;

      try {
        this._LoadBlock(path, copies, blockIndex, mirrorSplit: false);
      } catch (PoolFsException) {
        return; // prefetch is best-effort; the foreground read will surface real errors
      }
    }
  }

  public int Write(NodeHandle handle, ReadOnlySpan<byte> data, long offset, WriteMode mode) {
    this._RequireMounted();
    this._handles.Get(handle);
    throw this._WriteNotSupportedYet();
  }

  public void SetLength(NodeHandle handle, long length) {
    this._RequireMounted();
    this._handles.Get(handle);
    throw this._WriteNotSupportedYet();
  }

  public void Flush(NodeHandle handle) {
    this._RequireMounted();
    this._handles.Get(handle); // no dirty state exists before M2 — flush is a no-op
  }

  public void Close(NodeHandle handle) => this._handles.Close(handle);

  #endregion

  /// <summary>Aggregate statistics: shared physical volumes counted once, reserves subtracted (FR-STAT, FR-SPACE-SHARED).</summary>
  public FsStatistics StatFs() {
    this._RequireMounted();
    long free = 0, total = 0;
    foreach (var group in this._members.Where(m => m.Io.IsOnline).GroupBy(m => m.Io.PhysicalVolumeId, StringComparer.OrdinalIgnoreCase)) {
      var io = group.First().Io;
      var reserved = group.Sum(m => m.ReserveBytes);
      free += Math.Max(0, io.BytesFree - reserved);
      total += io.BytesTotal;
    }

    return new(total, free, this._cache.Pages.BlockSize);
  }

}
