using DivisonM.Vfs.Caching;

namespace DivisonM.Vfs.Engine;

/// <summary>One member as the engine sees it: its I/O backend plus manifest facts.</summary>
public sealed record EngineMember(IVolumeIO Io, MemberRole Role = MemberRole.Capacity, long ReserveBytes = 0);

/// <summary>
/// The VFS engine (CMP-VFS) over a set of pool members: presents the merged logical
/// namespace (FR-DIR), hides on-disk sidecars (FR-HIDE), serves block-aligned cached
/// reads with read-ahead (FR-RA) and mirror block routing (FR-MIRROR), reports
/// duplication-aware pool statistics (FR-STAT), and executes journalled, crash-safe
/// mutations (SAFE-WAL/SAFE-ORDER): every write is durable on all reachable copies
/// before it is acknowledged (M2 semantics — the tiered write-back cascade of M3
/// relaxes latency, never durability).
/// </summary>
public sealed class PoolFileSystem : IPoolFileSystem {

  private readonly Guid _poolId;
  private readonly IReadOnlyList<EngineMember> _members;
  private readonly CacheInstance _cache;
  private readonly PoolConfig _config;
  private readonly PlacementResolver _placement;
  private readonly HandleTable _handles = new();
  private readonly Journal _journal;
  private readonly WriteBufferManager _writeBuffer;
  private readonly PoolTrash _trash;
  private readonly Func<DateTime> _clock;
  private readonly long _readAheadMin;
  private readonly long _readAheadMax;
  private readonly bool _readAheadEnabled;
  private readonly bool _readAheadAdaptive;
  private readonly long _mirrorSplitThreshold;
  private MountOptions? _mountOptions;

  public PoolFileSystem(Guid poolId, IReadOnlyList<EngineMember> members, CacheInstance cache, PoolConfig effectiveConfig, Journal? journal = null, Func<DateTime>? clock = null) {
    this._poolId = poolId;
    this._members = members;
    this._cache = cache;
    this._config = effectiveConfig;
    this._journal = journal ?? new(new MemberJournalStore([.. members.Select(m => m.Io)]));
    this._clock = clock ?? (static () => DateTime.UtcNow);
    this._writeBuffer = new(cache, this._clock);
    this._trash = new([.. members.Select(m => m.Io)], this._journal, this._clock);
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
  public Journal Journal => this._journal;
  public WriteBufferManager WriteBuffer => this._writeBuffer;
  public PoolTrash Trash => this._trash;

  /// <summary>Builds the background workers for this pool (CMP-BG): owed-copy sync (with the deferred window) and the landing-zone drainer.</summary>
  public BackgroundScheduler CreateScheduler() {
    var write = this._config.Write;
    var deferWindow = (write?.Policy ?? WritePolicy.WriteBack) == WritePolicy.Deferred
      ? DurationSpec.Parse(write?.DeferWindow ?? "5s")
      : TimeSpan.Zero;
    var maxDefer = TimeSpan.FromSeconds(write?.MaxDeferSeconds ?? 30);
    var jobs = new List<IBackgroundJob> { new OwedSyncJob(this, deferWindow, maxDefer), new DrainJob(this) };
    if (this._config.Trash?.Enabled == true)
      jobs.Add(new TrashMaintenanceJob(this));

    return new(jobs);
  }

  /// <summary>Applies the configured trash retention and size cap, purging oldest first (§6.14).</summary>
  public int PurgeTrash() {
    var trashConfig = this._config.Trash;
    var retention = DurationSpec.Parse(trashConfig?.Retention ?? "7d");
    var maxSizeSpec = SizeSpec.Parse(trashConfig?.MaxSize ?? "5%");
    var maxSize = maxSizeSpec.ResolveBytes(this.StatFs().BytesTotal);
    return this._trash.Purge(retention, maxSize);
  }

  /// <summary>Restores a trashed item to its original path and re-establishes its duplication level (FR-TRASH).</summary>
  public void RestoreFromTrash(string originalPath) {
    this._RequireWritable();
    var normalized = PoolPaths.Normalize(originalPath);
    var restored = this._trash.Restore(normalized)
                   ?? throw new PoolFsException(PoolFsError.NotFound, $"No trash entry for '{originalPath}'");

    byte[] content;
    using (var source = restored.member.OpenRead(normalized, false)) {
      using var buffer = new MemoryStream();
      source.CopyTo(buffer);
      content = buffer.ToArray();
    }

    this._Invalidate(normalized);
    this._EnsureShadows(normalized, this._placement.ResolveCopies(normalized), content);
    this._Invalidate(normalized);
    DriveBender.Logger($" - Restored '{normalized}' from trash");
  }
  public bool IsMounted => this._mountOptions != null;
  public bool IsReadOnly => this._mountOptions?.ReadOnly ?? false;

  private IEnumerable<IVolumeIO> _Online => this._members.Where(m => m.Io.IsOnline).Select(m => m.Io);

  public void Mount(MountOptions options) {
    if (this._mountOptions != null)
      throw new PoolFsException(PoolFsError.Exists, "Pool is already mounted");

    if (!this._Online.Any())
      throw new PoolFsException(PoolFsError.Offline, "No pool member is online — refusing to mount");

    // recovery before serving: roll forward, reconcile, clean temps (FR-RECOVER)
    var report = new PoolRecovery([.. this._Online], this._journal).Run();
    if (report.AnythingDone)
      DriveBender.Logger($"Recovery: {report.RolledForward} rolled forward, {report.Reconciled} reconciled, {report.TempsRemoved} staging files removed");

    this._mountOptions = options;
  }

  public void Unmount() {
    if (this._mountOptions == null)
      return;

    // clean unmount: every owed copy is applied before the mount releases (FR-CLEAN-UNMOUNT)
    foreach (var path in this._writeBuffer.DirtyPaths)
      this.FlushPath(path);

    this._mountOptions = null;
  }

  public void Dispose() => this.Unmount();

  private void _RequireMounted() {
    if (this._mountOptions == null)
      throw new PoolFsException(PoolFsError.InvalidArgument, "Pool is not mounted");
  }

  private void _RequireWritable() {
    this._RequireMounted();
    if (this.IsReadOnly)
      throw new PoolFsException(PoolFsError.AccessDenied, "Pool is mounted read-only");
  }

  #region metadata

  public FileMeta GetAttributes(string path) {
    this._RequireMounted();
    var normalized = PoolPaths.Normalize(path);
    var key = new MetadataKey(this._poolId, normalized, MetadataKind.Stat);
    if (this._cache.Metadata.TryGet<FileMeta>(key, out var cached))
      return this._OverlayMeta(normalized, cached);

    var meta = this._StatUncached(normalized)
               ?? throw new PoolFsException(PoolFsError.NotFound, $"Path not found: {path}");

    this._cache.Metadata.Put(key, meta);
    return this._OverlayMeta(normalized, meta);
  }

  private FileMeta _OverlayMeta(string normalized, FileMeta meta) {
    if (meta.IsDirectory || !this._writeBuffer.IsDirty(normalized))
      return meta;

    return meta with { Length = this._writeBuffer.OverlayLength(normalized, meta.Length) };
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
    this._RequireWritable();
    var normalized = PoolPaths.Normalize(path);
    var copies = this._placement.ResolveCopies(normalized);
    if (copies.Count == 0)
      throw new PoolFsException(PoolFsError.NotFound, $"Path not found: {path}");

    foreach (var copy in copies)
      copy.Volume.SetTimestamps(normalized, copy.Shadow, patch.CreationTimeUtc, patch.LastWriteTimeUtc);

    this._cache.Metadata.InvalidatePath(this._poolId, normalized);
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

  #region namespace

  private bool _ParentExists(string normalized) {
    var parent = PoolPaths.GetParent(normalized);
    return parent.Length == 0 || this._Online.Any(m => m.FolderExists(parent, false));
  }

  public NodeHandle Create(string path, NodeKind kind, CreateFlags flags) {
    this._RequireWritable();
    var normalized = PoolPaths.Normalize(path);
    if (kind == NodeKind.Directory) {
      this.MakeDir(normalized);
      return NodeHandle.Invalid;
    }

    if (!this._ParentExists(normalized))
      throw new PoolFsException(PoolFsError.NotFound, $"Parent folder not found: {path}");

    var existing = this._placement.ResolveCopies(normalized);
    if (existing.Count > 0) {
      if ((flags & CreateFlags.Exclusive) != 0)
        throw new PoolFsException(PoolFsError.Exists, $"File already exists: {path}");

      var handleForExisting = this._handles.Open(normalized, AccessMode.ReadWrite).Handle;
      if ((flags & CreateFlags.Truncate) != 0)
        this.SetLength(handleForExisting, 0);

      return handleForExisting;
    }

    var target = this._placement.ChoosePrimaryTarget(0)
                 ?? throw new PoolFsException(PoolFsError.NoSpace, "No pool member can take the new file");

    var sequence = this._journal.LogIntent(JournalOp.Create, normalized, memberId: target.MemberId);
    var parent = PoolPaths.GetParent(normalized);
    if (parent.Length > 0)
      target.EnsureFolder(parent, false);

    using (var stream = target.OpenWrite(normalized, false, true))
      stream.Flush();

    this._EnsureShadows(normalized, [], content: []);
    this._journal.Complete(sequence, JournalOp.Create);
    this._Invalidate(normalized);
    return this._handles.Open(normalized, AccessMode.ReadWrite).Handle;
  }

  /// <summary>Brings a file up to its folder's duplication level D by creating missing shadow copies (SAFE-DUP).</summary>
  private void _EnsureShadows(string normalized, IReadOnlyList<PhysicalCopy> knownCopies, byte[] content) {
    var duplication = this._placement.DuplicationLevelFor(PoolPaths.GetParent(normalized));
    if (duplication < 2)
      return;

    var holders = knownCopies.Count > 0
      ? knownCopies.Select(c => c.Volume).ToList()
      : [.. this._Online.Where(m => m.FileExists(normalized, false) || m.FileExists(normalized, true))];

    while (holders.Count < duplication) {
      var target = this._placement.ChooseShadowTarget(content.LongLength, holders);
      if (target == null) {
        DriveBender.Logger($"[Warning]Duplication level {duplication} for '{normalized}' not placeable — no independent failure domain left; owed copies deferred (SAFE-PHYS)");
        return;
      }

      var parent = PoolPaths.GetParent(normalized);
      target.EnsureFolder(parent, true);
      using (var stream = target.OpenWrite(normalized, true, true)) {
        if (content.Length > 0)
          stream.Write(content, 0, content.Length);
        stream.Flush();
      }

      holders.Add(target);
    }
  }

  public void Rename(string from, string to, RenameFlags flags) {
    this._RequireWritable();
    var fromNormalized = PoolPaths.Normalize(from);
    var toNormalized = PoolPaths.Normalize(to);
    var copies = this._placement.ResolveCopies(fromNormalized);
    if (copies.Count == 0)
      throw new PoolFsException(PoolFsError.NotFound, $"Path not found: {from}");

    if (!this._ParentExists(toNormalized))
      throw new PoolFsException(PoolFsError.NotFound, $"Target parent folder not found: {to}");

    var targetCopies = this._placement.ResolveCopies(toNormalized);
    if (targetCopies.Count > 0 && (flags & RenameFlags.ReplaceExisting) == 0)
      throw new PoolFsException(PoolFsError.Exists, $"Target already exists: {to}");

    this.FlushPath(fromNormalized); // pending mutations land under the old name first

    var sequence = this._journal.LogIntent(JournalOp.Rename, fromNormalized, toNormalized);

    // overwrite-on-rename removes every copy of the old target first (no orphans)
    foreach (var stale in targetCopies)
      stale.Volume.Delete(toNormalized, stale.Shadow);

    // namespace-atomic per member: the name flips via rename on every member holding a copy (FR-RENAME)
    foreach (var copy in copies) {
      var parent = PoolPaths.GetParent(toNormalized);
      if (parent.Length > 0)
        copy.Volume.EnsureFolder(parent, false);
      if (copy.Shadow)
        copy.Volume.EnsureFolder(parent, true);

      copy.Volume.AtomicReplace(fromNormalized, toNormalized, copy.Shadow);
    }

    this._journal.Complete(sequence, JournalOp.Rename);
    this._handles.RenamePath(fromNormalized, toNormalized);
    this._Invalidate(fromNormalized);
    this._Invalidate(toNormalized);
  }

  public void Unlink(string path) {
    this._RequireWritable();
    var normalized = PoolPaths.Normalize(path);
    var copies = this._placement.ResolveCopies(normalized);
    if (copies.Count == 0)
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {path}");

    // pending buffered mutations are moot once the file dies; their intents complete with the delete
    var discarded = this._writeBuffer.Drain(normalized);

    var effective = ConfigResolver.ResolveForFolder(this._config, PoolPaths.GetParent(normalized));
    if (effective.Trash?.Enabled == true) {
      // recoverable delete: all copies move to the hidden pool trash instead of dying (FR-TRASH)
      this._trash.MoveToTrash(normalized, copies, effective.Trash.DropDuplicatesInTrash ?? true);
      if (discarded != null)
        foreach (var staleSequence in discarded.Value.journalSequences)
          this._journal.Complete(staleSequence, JournalOp.Write);

      this._Invalidate(normalized);
      return;
    }

    var sequence = this._journal.LogIntent(JournalOp.Delete, normalized);

    // every primary and shadow copy goes — no orphans remain (FR-DELETE)
    foreach (var member in this._Online)
    foreach (var shadow in new[] { false, true })
      if (member.FileExists(normalized, shadow))
        member.Delete(normalized, shadow);

    this._journal.Complete(sequence, JournalOp.Delete);
    if (discarded != null)
      foreach (var staleSequence in discarded.Value.journalSequences)
        this._journal.Complete(staleSequence, JournalOp.Write);

    this._Invalidate(normalized);
  }

  public void MakeDir(string path) {
    this._RequireWritable();
    var normalized = PoolPaths.Normalize(path);
    if (normalized.Length == 0)
      throw new PoolFsException(PoolFsError.Exists, "The pool root always exists");

    if (!this._ParentExists(normalized))
      throw new PoolFsException(PoolFsError.NotFound, $"Parent folder not found: {path}");

    if (this._Online.Any(m => m.FolderExists(normalized, false)) || this._placement.ResolveCopies(normalized).Count > 0)
      throw new PoolFsException(PoolFsError.Exists, $"Path already exists: {path}");

    var target = this._placement.ChoosePrimaryTarget(0)
                 ?? throw new PoolFsException(PoolFsError.NoSpace, "No pool member can take the new folder");

    var sequence = this._journal.LogIntent(JournalOp.MakeDir, normalized, memberId: target.MemberId);
    target.EnsureFolder(normalized, false);
    if (this._placement.DuplicationLevelFor(normalized) >= 2)
      target.EnsureFolder(normalized, true); // enable the duplication container

    this._journal.Complete(sequence, JournalOp.MakeDir);
    this._Invalidate(normalized);
  }

  public void RemoveDir(string path) {
    this._RequireWritable();
    var normalized = PoolPaths.Normalize(path);
    if (normalized.Length == 0)
      throw new PoolFsException(PoolFsError.AccessDenied, "The pool root cannot be removed");

    if (this.ReadDirectory(normalized).Count > 0)
      throw new PoolFsException(PoolFsError.NotEmpty, $"Folder is not empty: {path}");

    var sequence = this._journal.LogIntent(JournalOp.RemoveDir, normalized);
    foreach (var member in this._Online) {
      if (member.FolderExists(normalized, true))
        member.DeleteFolder(normalized, true);
      if (member.FolderExists(normalized, false))
        member.DeleteFolder(normalized, false);
    }

    this._journal.Complete(sequence, JournalOp.RemoveDir);
    this._Invalidate(normalized);
  }

  private void _Invalidate(string normalized) {
    this._placement.Invalidate(normalized);
    this._cache.InvalidatePath(this._poolId, normalized);
  }

  #endregion

  #region data

  public NodeHandle Open(string path, AccessMode mode, ShareMode share) {
    this._RequireMounted();
    if ((mode & AccessMode.Write) != 0)
      this._RequireWritable();

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

      var length = this._writeBuffer.OverlayLength(path, copies[0].Volume.Stat(path, copies[0].Shadow)?.Length ?? 0);
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
      var block = this._writeBuffer.OverlayBlock(path, blockIndex, blockSize, this._LoadBlock(path, copies, blockIndex, mirrorSplit));
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
    this._RequireWritable();
    var open = this._handles.Get(handle);
    if ((open.Access & AccessMode.Write) == 0)
      throw new PoolFsException(PoolFsError.AccessDenied, "Handle is not open for writing");
    if (offset < 0)
      throw new PoolFsException(PoolFsError.InvalidArgument, "Negative offset");

    var bytes = data.ToArray();
    open.File.Lock.EnterWriteLock();
    try {
      var path = open.File.Path;
      var copies = this._placement.ResolveCopies(path);
      if (copies.Count == 0)
        throw new PoolFsException(PoolFsError.NotFound, $"File vanished: {path}");

      if (mode == WriteMode.Append)
        offset = this._writeBuffer.OverlayLength(path, copies[0].Volume.Stat(path, copies[0].Shadow)?.Length ?? 0);

      var effective = ConfigResolver.ResolveForFolder(this._config, PoolPaths.GetParent(path));
      var policy = effective.Write?.Policy ?? WritePolicy.WriteBack;
      var volatileAck = policy == WritePolicy.Performance && (effective.Write?.AcceptVolatileAck ?? false);

      // performance + acceptVolatileAck: the ack may come from RAM alone — an explicit,
      // per-folder opt-in (SAFE-RAM); fsync still forces durability (SAFE-FSYNC)
      if (volatileAck && this._writeBuffer.StageWrite(path, offset, bytes, 0, 0)) {
        this._cache.Pages.InvalidatePath(this._poolId, path);
        this._cache.Metadata.InvalidatePath(this._poolId, path);
        return bytes.Length;
      }

      var requiredCopies = policy == WritePolicy.WriteThrough
        ? copies.Count
        : Math.Min(copies.Count, ConfigValidator.EffectiveMinCopiesBeforeAck(effective.Write, effective.Duplication));

      this._RequireAckQuorum(path, copies.Count);

      // intent → mutate the ack set durably → (write-through: complete now; write-back /
      // deferred: the intent stays open until the owed copies are applied, so a crash
      // in the gap reconciles from the durable primary) (SAFE-ORDER, FR-WT, FR-WB)
      var sequence = this._journal.LogIntent(JournalOp.Write, path, offset: offset, length: bytes.Length);
      for (var i = 0; i < requiredCopies; ++i) {
        var copy = copies[i];
        using var stream = copy.Volume.OpenWrite(path, copy.Shadow, false);
        stream.Seek(offset, SeekOrigin.Begin);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(); // durability barrier per copy (SAFE-FSYNC)
      }

      if (requiredCopies >= copies.Count)
        this._journal.Complete(sequence, JournalOp.Write);
      else if (!this._writeBuffer.StageWrite(path, offset, bytes, sequence, requiredCopies)) {
        // write-buffer backpressure: degrade to write-through instead of growing RAM (FR-BACKP)
        for (var i = requiredCopies; i < copies.Count; ++i) {
          var copy = copies[i];
          using var stream = copy.Volume.OpenWrite(path, copy.Shadow, false);
          stream.Seek(offset, SeekOrigin.Begin);
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush();
        }

        this._journal.Complete(sequence, JournalOp.Write);
      }

      // coherency: a read after this write must return the new bytes (SAFE-COHERE)
      this._cache.Pages.InvalidatePath(this._poolId, path);
      this._cache.Metadata.InvalidatePath(this._poolId, path);
      return bytes.Length;
    } finally {
      open.File.Lock.ExitWriteLock();
    }
  }

  /// <summary>Refuses an ack when fewer copies are reachable than the folder's effective minCopiesBeforeAck (SAFE-LZ).</summary>
  private void _RequireAckQuorum(string path, int reachableCopies) {
    var effective = ConfigResolver.ResolveForFolder(this._config, PoolPaths.GetParent(path));
    var required = ConfigValidator.EffectiveMinCopiesBeforeAck(effective.Write, effective.Duplication);
    if (reachableCopies < required)
      throw new PoolFsException(PoolFsError.Offline, $"Only {reachableCopies} of the required {required} copies of '{path}' are reachable — refusing to acknowledge (minCopiesBeforeAck)");
  }

  public void SetLength(NodeHandle handle, long length) {
    this._RequireWritable();
    var open = this._handles.Get(handle);
    if ((open.Access & AccessMode.Write) == 0)
      throw new PoolFsException(PoolFsError.AccessDenied, "Handle is not open for writing");
    if (length < 0)
      throw new PoolFsException(PoolFsError.InvalidArgument, "Negative length");

    open.File.Lock.EnterWriteLock();
    try {
      var path = open.File.Path;
      this.FlushPath(path); // pending buffered writes apply before the truncate so ordering stays linear
      var copies = this._placement.ResolveCopies(path);
      if (copies.Count == 0)
        throw new PoolFsException(PoolFsError.NotFound, $"File vanished: {path}");

      var sequence = this._journal.LogIntent(JournalOp.Truncate, path, length: length);
      foreach (var copy in copies)
        copy.Volume.Truncate(path, copy.Shadow, length); // grows zero-filled or shrinks on all copies (FR-TRUNC)

      this._journal.Complete(sequence, JournalOp.Truncate);
      this._cache.Pages.InvalidatePath(this._poolId, path);
      this._cache.Metadata.InvalidatePath(this._poolId, path);
    } finally {
      open.File.Lock.ExitWriteLock();
    }
  }

  public void Flush(NodeHandle handle) {
    this._RequireMounted();
    var open = this._handles.Get(handle);
    this.FlushPath(open.File.Path); // fsync is an absolute durability barrier in every mode (SAFE-FSYNC)
  }

  /// <summary>
  /// Applies every buffered mutation of a path durably to all its copies and completes
  /// the open journal intents. Idempotent; no-op for clean files.
  /// </summary>
  public void FlushPath(string path) {
    var normalized = PoolPaths.Normalize(path);
    var drained = this._writeBuffer.Drain(normalized);
    if (drained == null)
      return;

    var (ops, journalSequences, _) = drained.Value;
    if (ops.Count > 0) {
      var copies = this._placement.ResolveCopies(normalized);
      var volatileSequence = journalSequences.Count == 0 && copies.Count > 0
        ? this._journal.LogIntent(JournalOp.Write, normalized)
        : 0;

      foreach (var copy in copies) {
        using var stream = copy.Volume.OpenWrite(normalized, copy.Shadow, false);
        foreach (var op in ops) {
          if (op.TruncateLength is { } truncateLength) {
            stream.SetLength(truncateLength);
            continue;
          }

          stream.Seek(op.Offset, SeekOrigin.Begin);
          stream.Write(op.Data!, 0, op.Data!.Length);
        }

        stream.Flush(); // durability barrier per copy (SAFE-FSYNC)
      }

      if (volatileSequence != 0)
        this._journal.Complete(volatileSequence, JournalOp.Write);
    }

    foreach (var sequence in journalSequences)
      this._journal.Complete(sequence, JournalOp.Write);

    this._cache.Pages.InvalidatePath(this._poolId, normalized);
    this._cache.Metadata.InvalidatePath(this._poolId, normalized);
  }

  /// <summary>
  /// Drains one clean, closed file from a fast-tier member down to capacity (FR-LZ-DRAIN):
  /// whole-file copy via temp + atomic rename under a journalled Drain intent, duplication
  /// re-established, then the fast-tier original is freed. Returns false when nothing is
  /// drainable right now.
  /// </summary>
  public bool DrainOneLandingFile() {
    if (this._mountOptions == null)
      return false;

    foreach (var landing in this._members.Where(m => m is { Role: MemberRole.Landing, Io.IsOnline: true }).Select(m => m.Io)) {
      foreach (var path in this._WalkFiles(landing)) {
        if (this._writeBuffer.IsDirty(path) || this._handles.IsOpen(path))
          continue; // only clean, closed files move (the balancer rule of §6.10 applies here too)

        var copies = this._placement.ResolveCopies(path);
        var holders = copies.Select(c => c.Volume).ToArray();
        var size = landing.Stat(path, false)?.Length ?? 0;
        var target = this._placement.ChooseDrainTarget(size, holders.Where(h => h.MemberId != landing.MemberId));
        if (target == null)
          continue;

        var sequence = this._journal.LogIntent(JournalOp.Drain, path, memberId: target.MemberId);

        byte[] content;
        using (var source = landing.OpenRead(path, false)) {
          using var buffer = new MemoryStream();
          source.CopyTo(buffer);
          content = buffer.ToArray();
        }

        var parent = PoolPaths.GetParent(path);
        if (parent.Length > 0)
          target.EnsureFolder(parent, false);

        var temp = path + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
        using (var stream = target.OpenWrite(temp, false, true)) {
          stream.Write(content, 0, content.Length);
          stream.Flush();
        }

        target.AtomicReplace(temp, path, false);
        landing.Delete(path, false); // free the fast tier only after the durable capacity copy exists
        this._journal.Complete(sequence, JournalOp.Drain);

        this._Invalidate(path);
        this._EnsureShadows(path, this._placement.ResolveCopies(path), content);
        this._Invalidate(path);
        DriveBender.Logger($" - Drained '{path}' from '{landing.DisplayName}' to '{target.DisplayName}' ({content.Length} bytes)");
        return true;
      }
    }

    return false;
  }

  /// <summary>All primary files on one member, walked raw (shadow containers and sidecars skipped).</summary>
  private IEnumerable<string> _WalkFiles(IVolumeIO member) {
    var stack = new Stack<string>();
    stack.Push("");
    while (stack.Count > 0) {
      var folder = stack.Pop();
      VolumeEntry[] entries;
      try {
        entries = [.. member.List(folder, false)];
      } catch (PoolFsException) {
        continue;
      }

      foreach (var entry in entries) {
        if (PoolPaths.IsHiddenName(entry.Name))
          continue;

        var childPath = folder.Length == 0 ? entry.Name : $"{folder}/{entry.Name}";
        if (entry.IsDirectory)
          stack.Push(childPath);
        else
          yield return childPath;
      }
    }
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
