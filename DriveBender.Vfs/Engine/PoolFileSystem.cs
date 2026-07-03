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
  private PoolConfig _config;
  private readonly PlacementResolver _placement;
  private readonly HandleTable _handles = new();
  private readonly Journal _journal;
  private readonly WriteBufferManager _writeBuffer;
  private readonly PoolTrash _trash;
  private readonly IntegrityService _integrity;
  private readonly ActivityFeed _activity;
  private readonly ShadowNamespace _shadow = new();
  private readonly MemberWatcher _watcher;
  private readonly Func<DateTime> _clock;
  private long _readAheadMin;
  private long _readAheadMax;
  private bool _readAheadEnabled;
  private bool _readAheadAdaptive;
  private long _mirrorSplitThreshold;
  private MemberLossPolicy _memberLossPolicy;
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
    this._integrity = new([.. members.Select(m => m.Io)], effectiveConfig.Integrity?.OnExternalEdit ?? ExternalEditPolicy.AcceptNewest);
    this._activity = new(clock: this._clock);
    this._placement = new(
      poolId,
      [.. members.Select(m => m.Io)],
      cache.Metadata,
      effectiveConfig,
      members.ToDictionary(m => m.Io.MemberId, m => m.Role));

    this._watcher = new([.. members.Select(m => m.Io)]);
    this._watcher.MemberLost += this._OnMemberLost;
    this._watcher.MemberReturned += this._OnMemberReturned;
    this._ApplyRuntimeConfig(effectiveConfig);
  }

  private void _ApplyRuntimeConfig(PoolConfig config) {
    var readAhead = config.ReadAhead;
    this._readAheadEnabled = readAhead?.Enabled ?? true;
    this._readAheadMin = SizeSpec.ParseBytes(readAhead?.MinWindow ?? "1MiB");
    this._readAheadMax = SizeSpec.ParseBytes(readAhead?.MaxWindow ?? "8MiB");
    this._readAheadAdaptive = readAhead?.Adaptive ?? true;
    this._mirrorSplitThreshold = SizeSpec.ParseBytes(config.Io?.MirrorReadSplitThreshold ?? "8MiB");
    this._memberLossPolicy = config.Resilience?.OnMemberLoss ?? MemberLossPolicy.RetainMetadata;
  }

  public PlacementResolver Placement => this._placement;
  public Journal Journal => this._journal;
  public WriteBufferManager WriteBuffer => this._writeBuffer;
  public PoolTrash Trash => this._trash;
  public IntegrityService Integrity => this._integrity;
  public ActivityFeed Activity => this._activity;
  public Caching.CacheInstance Cache => this._cache;
  public MemberWatcher Watcher => this._watcher;
  public ShadowNamespace Shadow => this._shadow;
  public MemberLossPolicy MemberLossPolicy => this._memberLossPolicy;

  /// <summary>Polls member reachability and reacts per the drive-loss policy; the host/scheduler drives this (§10 SAFE-DEGRADE).</summary>
  public bool PollMembers() => this._watcher.Poll();

  /// <summary>
  /// Applies non-structural config changes to a mounted pool without unmount (CFG.reload):
  /// write policy, tiers, read-ahead, drive-loss policy, and cache sizing. Shrinking the
  /// cache flushes dirty write-buffer data down to the new cap first, never dropping it
  /// (SAFE-NOLOSS). Membership changes are not reload-able and are rejected.
  /// </summary>
  public void ReloadConfig(PoolConfig newConfig) {
    ConfigValidator.Validate(newConfig, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    // shrinking caches must not strand dirty data: flush everything owed first (SAFE-NOLOSS)
    if (this._writeBuffer.DirtyPaths.Count > 0)
      foreach (var path in this._writeBuffer.DirtyPaths)
        this.FlushPath(path);

    this._config = newConfig;
    this._ApplyRuntimeConfig(newConfig);

    // placement reads its tuning live from the config reference it was given; refresh caches so new policy takes effect
    this._cache.Metadata.InvalidatePool(this._poolId);
    this._placement.UpdateConfig(newConfig);
    this._activity.Publish(ActivityKind.Recovery, "", reason: "config reloaded");
    DriveBender.Logger("Configuration reloaded live");
  }

  /// <summary>
  /// Applies changed member roles live (reconfigure storage without remount): new writes land
  /// per the new tier layout right away; already-placed data moves via the drainer/rebalancer.
  /// </summary>
  public void UpdateMemberRoles(IReadOnlyDictionary<Guid, MemberRole> roles) {
    this._placement.UpdateRoles(roles);
    this._placement.InvalidateAll();
    this._activity.Publish(ActivityKind.Recovery, "", reason: "member roles reloaded");
    DriveBender.Logger("Member roles reloaded live");
  }

  private void _OnMemberLost(IVolumeIO member) {
    this._activity.Publish(ActivityKind.Recovery, "", reason: $"member lost: {member.DisplayName}");
    if (this._memberLossPolicy == MemberLossPolicy.DiscardInaccessible) {
      // drop cached metadata so the next listing reflects only surviving copies…
      this._cache.Metadata.InvalidatePool(this._poolId);
      this._placement.InvalidateAll();

      // …and prune the shadow namespace of paths with no accessible copy left
      foreach (var path in this._shadow.AllPaths())
        if (this._placement.ResolveCopies(path).Count == 0)
          this._shadow.Remove(path);
    }
    // retain-metadata: keep every cache and the shadow namespace so metadata stays complete
  }

  private void _OnMemberReturned(IVolumeIO member) {
    // a returned member may hold newer/owed data; drop caches and reconcile (SAFE-OFFLINE)
    this._cache.Metadata.InvalidatePool(this._poolId);
    this._placement.InvalidateAll();
    if (this._config.Integrity?.ChecksumDb != false)
      this._integrity.QuickScan(this._Invalidate);
  }

  /// <summary>Dashboard counters for this pool (OPS-METRICS).</summary>
  public PoolMetrics GetMetrics()
    => this._activity.Snapshot(this._cache.Pages.GetStatistics(this._poolId), this._writeBuffer.DirtyPaths.Count);

  /// <summary>Full checksum scrub: bit-rot repair and out-of-band reconciliation (FR-SCRUB, SAFE-OOB).</summary>
  public IReadOnlyList<IntegrityIssue> RunScrub() {
    var issues = this._integrity.ScrubAll(this._Invalidate);
    foreach (var issue in issues)
      this._activity.Publish(ActivityKind.Scrub, issue.Path, reason: $"{issue.Kind}: {issue.Message}");

    return issues;
  }

  /// <summary>Builds the background workers for this pool (CMP-BG): owed-copy sync (with the deferred window) and the landing-zone drainer.</summary>
  public BackgroundScheduler CreateScheduler() {
    var write = this._config.Write;
    var deferWindow = (write?.Policy ?? WritePolicy.WriteBack) == WritePolicy.Deferred
      ? DurationSpec.Parse(write?.DeferWindow ?? "5s")
      : TimeSpan.Zero;
    var maxDefer = TimeSpan.FromSeconds(write?.MaxDeferSeconds ?? 30);
    var jobs = new List<IBackgroundJob> { new OwedSyncJob(this, deferWindow, maxDefer), new DrainJob(this), new MemberWatchJob(this) };
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
    if (report.AnythingDone) {
      DriveBender.Logger($"Recovery: {report.RolledForward} rolled forward, {report.Reconciled} reconciled, {report.TempsRemoved} staging files removed");
      this._activity.Publish(ActivityKind.Recovery, "", report.RolledForward + report.Reconciled, reason: "journal replay on mount");
    }

    // externally-modified members are caught before serving stale data (FR-OOB-MOUNT)
    if (this._config.Integrity?.ChecksumDb != false) {
      var oob = this._integrity.QuickScan(this._Invalidate);
      foreach (var issue in oob)
        DriveBender.Logger($"[Integrity]{issue.Kind}: {issue.Path} — {issue.Message}");
    }

    this._mountOptions = options;
  }

  public void Unmount() {
    if (this._mountOptions == null)
      return;

    // clean unmount: every owed copy is applied before the mount releases (FR-CLEAN-UNMOUNT)
    foreach (var path in this._writeBuffer.DirtyPaths)
      this.FlushPath(path);

    this._integrity.SaveAll();
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

    var meta = this._StatUncached(normalized);
    if (meta == null) {
      // retain-metadata: a path whose only copy vanished is still visible from the shadow namespace (§10 SAFE-DEGRADE)
      if (this._memberLossPolicy == MemberLossPolicy.RetainMetadata && this._shadow.Get(normalized) is { } remembered)
        return new(remembered.Length, DateTime.MinValue, remembered.LastWriteTimeUtc, remembered.Kind == NodeKind.Directory ? FileAttributes.Directory : FileAttributes.Normal);

      throw new PoolFsException(PoolFsError.NotFound, $"Path not found: {path}");
    }

    this._cache.Metadata.Put(key, meta.Value);
    this._shadow.Record(normalized, new(meta.Value.IsDirectory ? NodeKind.Directory : NodeKind.File, meta.Value.Length, meta.Value.LastWriteTimeUtc));
    return this._OverlayMeta(normalized, meta.Value);
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

    // retain-metadata: complete the listing with remembered entries whose members have dropped out (§10 SAFE-DEGRADE)
    if (this._memberLossPolicy == MemberLossPolicy.RetainMetadata && (folderSeen || this._shadow.Get(normalized)?.Kind == NodeKind.Directory))
      foreach (var remembered in this._shadow.Children(normalized))
        if (!entries.ContainsKey(remembered.Name)) {
          entries[remembered.Name] = remembered;
          folderSeen = true;
        }

    if (!folderSeen)
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {path}");

    IReadOnlyList<DirEntry> result = [.. entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    this._cache.Metadata.Put(key, result);

    // remember the live namespace so it survives a later member loss
    this._shadow.Record(normalized, new(NodeKind.Directory, 0, DateTime.MinValue));
    foreach (var entry in result)
      this._shadow.Record(normalized.Length == 0 ? entry.Name : $"{normalized}/{entry.Name}", new(entry.Kind, entry.Length, entry.LastWriteTimeUtc));

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

    this._integrity.RecordWholeFile(target, normalized, false, []);
    this._EnsureShadows(normalized, [], content: []);
    this._journal.Complete(sequence, JournalOp.Create);
    this._Invalidate(normalized);
    this._shadow.Record(normalized, new(NodeKind.File, 0, this._clock()));
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

      this._integrity.RecordWholeFile(target, normalized, true, content);
      holders.Add(target);
    }
  }

  public void Rename(string from, string to, RenameFlags flags) {
    this._RequireWritable();
    var fromNormalized = PoolPaths.Normalize(from);
    var toNormalized = PoolPaths.Normalize(to);
    var copies = this._placement.ResolveCopies(fromNormalized);
    if (copies.Count == 0) {
      // not a file — folders resolve by their directory presence (FR-RENAME for directories)
      if (this._Online.Any(m => m.FolderExists(fromNormalized, false))) {
        this._RenameFolder(fromNormalized, toNormalized);
        return;
      }

      throw new PoolFsException(PoolFsError.NotFound, $"Path not found: {from}");
    }

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
    this._integrity.RenameFile(fromNormalized, toNormalized);
    this._handles.RenamePath(fromNormalized, toNormalized);
    this._shadow.Rename(fromNormalized, toNormalized);
    this._Invalidate(fromNormalized);
    this._Invalidate(toNormalized);
  }

  /// <summary>
  /// Renames a directory subtree (FR-RENAME for folders): the folder flips via one directory
  /// rename on every member that holds it — the embedded shadow folders travel along — then
  /// checksums, open handles, the shadow namespace and caches follow the new prefix.
  /// </summary>
  private void _RenameFolder(string fromNormalized, string toNormalized) {
    if (toNormalized.Length == 0 || fromNormalized.Length == 0)
      throw new PoolFsException(PoolFsError.AccessDenied, "The pool root cannot be renamed");
    if ((toNormalized + "/").StartsWith(fromNormalized + "/", StringComparison.OrdinalIgnoreCase))
      throw new PoolFsException(PoolFsError.InvalidArgument, $"Cannot move a folder into itself: {fromNormalized} → {toNormalized}");
    if (!this._ParentExists(toNormalized))
      throw new PoolFsException(PoolFsError.NotFound, $"Target parent folder not found: {toNormalized}");
    if (this._Online.Any(m => m.FolderExists(toNormalized, false) || m.FileExists(toNormalized, false)))
      throw new PoolFsException(PoolFsError.Exists, $"Target already exists: {toNormalized}");

    // dirty children must land under the old name before the tree moves (SAFE-NOLOSS)
    var fromPrefix = fromNormalized + "/";
    foreach (var dirty in this._writeBuffer.DirtyPaths.Where(p => p.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
      this.FlushPath(dirty);

    var sequence = this._journal.LogIntent(JournalOp.Rename, fromNormalized, toNormalized);

    var parent = PoolPaths.GetParent(toNormalized);
    foreach (var member in this._Online.Where(m => m.FolderExists(fromNormalized, false))) {
      if (parent.Length > 0)
        member.EnsureFolder(parent, false);

      member.RenameFolder(fromNormalized, toNormalized);
    }

    this._journal.Complete(sequence, JournalOp.Rename);
    this._integrity.RenameSubtree(fromNormalized, toNormalized);
    this._handles.RenameSubtree(fromNormalized, toNormalized);
    this._shadow.Rename(fromNormalized, toNormalized);

    // every cached child listing/placement under the old prefix is stale — drop the pool's caches
    this._cache.Metadata.InvalidatePool(this._poolId);
    this._placement.InvalidateAll();
    DriveBender.Logger($"Renamed folder '{fromNormalized}' to '{toNormalized}' across {this._Online.Count(m => m.FolderExists(toNormalized, false))} member(s)");
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
      this._integrity.InvalidateFile(normalized);
      if (discarded != null)
        foreach (var staleSequence in discarded.Value.journalSequences)
          this._journal.Complete(staleSequence, JournalOp.Write);

      this._Invalidate(normalized);
      this._shadow.Remove(normalized);
      return;
    }

    var sequence = this._journal.LogIntent(JournalOp.Delete, normalized);

    // every primary and shadow copy goes — no orphans remain (FR-DELETE)
    foreach (var member in this._Online)
    foreach (var shadow in new[] { false, true })
      if (member.FileExists(normalized, shadow))
        member.Delete(normalized, shadow);

    this._journal.Complete(sequence, JournalOp.Delete);
    this._integrity.InvalidateFile(normalized);
    if (discarded != null)
      foreach (var staleSequence in discarded.Value.journalSequences)
        this._journal.Complete(staleSequence, JournalOp.Write);

    this._Invalidate(normalized);
    this._shadow.Remove(normalized);
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
    this._shadow.Record(normalized, new(NodeKind.Directory, 0, this._clock()));
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
    this._shadow.Remove(normalized);
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
      this._activity.Publish(ActivityKind.Read, path, count, fromMember: copies[0].Volume.DisplayName, reason: "user I/O");

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
      IReadOnlyList<PhysicalCopy> copies = this._placement.ResolveCopies(path);
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

      // copies on members without a durable flush can never satisfy the ack quorum and
      // are always completed asynchronously (SAFE-REMOTE); order them behind the rest
      var orderedCopies = copies.OrderByDescending(c => WholeFilePublisher.CanSatisfyAckQuorum(c.Volume)).ToArray();
      var eligibleCount = orderedCopies.Count(c => WholeFilePublisher.CanSatisfyAckQuorum(c.Volume));
      copies = orderedCopies;

      var requiredCopies = policy == WritePolicy.WriteThrough
        ? eligibleCount
        : Math.Min(eligibleCount, ConfigValidator.EffectiveMinCopiesBeforeAck(effective.Write, effective.Duplication));

      this._RequireAckQuorum(path, eligibleCount);

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
      else if (policy == WritePolicy.WriteThrough) {
        // write-through with weak-flush members: apply their copies now (best effort), the
        // intent stays open so recovery reconciles them if they were lost (SAFE-REMOTE)
        for (var i = requiredCopies; i < copies.Count; ++i) {
          var copy = copies[i];
          using var stream = copy.Volume.OpenWrite(path, copy.Shadow, false);
          stream.Seek(offset, SeekOrigin.Begin);
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush();
        }

        this._journal.Complete(sequence, JournalOp.Write);
      }
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

      this._activity.Publish(ActivityKind.Write, path, bytes.Length, toMember: copies.Count > 0 ? copies[0].Volume.DisplayName : null, reason: policy.ToString());

      // coherency: a read after this write must return the new bytes (SAFE-COHERE)
      this._integrity.InvalidateFile(path);
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
      this._integrity.InvalidateFile(path);
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

    this._integrity.InvalidateFile(normalized);
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

        WholeFilePublisher.Publish(target, path, false, content);
        landing.Delete(path, false); // free the fast tier only after the durable capacity copy exists
        this._journal.Complete(sequence, JournalOp.Drain);
        this._integrity.InvalidateFile(path);
        this._integrity.RecordWholeFile(target, path, false, content);
        this._activity.Publish(ActivityKind.Drain, path, content.Length, landing.DisplayName, target.DisplayName, "landing-zone drain");

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

  /// <summary>
  /// Aggregate statistics: shared physical volumes counted once, reserves subtracted
  /// (FR-STAT, FR-SPACE-SHARED). Members whose backend cannot report capacity signal it
  /// with BytesTotal == 0 and are excluded from the aggregate — never counted as zero or
  /// infinite (documented FR-STAT convention).
  /// </summary>
  public FsStatistics StatFs() {
    this._RequireMounted();
    long free = 0, total = 0;
    foreach (var group in this._members.Where(m => m.Io.IsOnline).GroupBy(m => m.Io.PhysicalVolumeId, StringComparer.OrdinalIgnoreCase)) {
      var io = group.First().Io;
      if (io.BytesTotal == 0)
        continue; // capacity unknown (remote service) — excluded from the aggregate

      var reserved = group.Sum(m => m.ReserveBytes);
      free += Math.Max(0, io.BytesFree - reserved);
      total += io.BytesTotal;
    }

    return new(total, free, this._cache.Pages.BlockSize);
  }

}
