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
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _prefetching = new(StringComparer.OrdinalIgnoreCase);

  // FR-STAGED-WRITE: a file between Create and its last Close lives under a temp physical name
  // (*.TEMP.$DRIVEBENDER, hidden on disk), so it never looks fully written until it is. The value
  // is the still-open Create journal sequence — the atomic temp→final rename is the LAST action
  // before that intent completes; a crash before it leaves only temps the recovery sweep removes.
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _staging = new(StringComparer.OrdinalIgnoreCase);

  private static string _StagedNameOf(string normalized) => normalized + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;

  /// <summary>The physical name data ops must use: the staged temp while the file is being written, the real name after.</summary>
  private string _DataName(string normalized) => this._staging.ContainsKey(normalized) ? _StagedNameOf(normalized) : normalized;

  // SAFE-OFFLINE: namespace changes an offline member missed (deletes/renames) — replayed on
  // its return so stale files never resurrect into the pool
  private readonly TombstoneLog _tombstones;

  // FR-HEAL: paths whose duplication level must be re-established (a member returned, or a
  // scan found owed copies); drained incrementally by the background HealJob
  private readonly System.Collections.Concurrent.ConcurrentQueue<string> _healQueue = new();
  private int _healScanRequested;
  private IEnumerator<string>? _healScan; // advanced only by HealStep (single pump thread)

  // degraded-write warnings deduplicate per path; cleared when membership changes
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _degradedAckWarned = new(StringComparer.OrdinalIgnoreCase);

  // FR-STRIPE-READY: outstanding I/O per member — the stripe selector routes each block to the
  // storage that is READY, so a fast/idle SSD naturally takes more blocks than a slow/busy HDD
  private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _memberLoad = new();

  private void _BeginIo(Guid memberId) => this._memberLoad.AddOrUpdate(memberId, 1, static (_, v) => v + 1);
  private void _EndIo(Guid memberId) => this._memberLoad.AddOrUpdate(memberId, 0, static (_, v) => Math.Max(0, v - 1));

  /// <summary>Readiness score: queued work dominates, measured latency breaks ties — lower is readier.</summary>
  private double _LoadScore(IVolumeIO volume)
    => (this._memberLoad.TryGetValue(volume.MemberId, out var inflight) ? inflight : 0) * 1000.0
       + (volume is MeasuredVolumeIO { Samples: > 0 } measured ? measured.AverageLatencyMs : 0.0);
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

    this._tombstones = new([.. members.Select(m => m.Io)]);
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
    this._activity.Publish(ActivityKind.Recovery, "", reason: $"member returned: {member.DisplayName}");
    this._degradedAckWarned.Clear();

    // 0) heal its stale journal mirror so a compaction it missed can't resurrect old intents (SAFE-OFFLINE)
    this._journal.ReconcileMirrors();

    // 1) namespace changes it missed apply first (deletes/renames — no ghost resurrection, SAFE-OFFLINE)
    var replayed = this._tombstones.ReplayFor(member, [.. this._members.Select(m => m.Io.MemberId)]);
    if (replayed > 0)
      DriveBender.Logger($"Applied {replayed} missed namespace change(s) to returned member '{member.DisplayName}'");

    // 2) a returned member may hold newer/owed data; drop caches so listings see its copies
    this._cache.Metadata.InvalidatePool(this._poolId);
    this._placement.InvalidateAll();

    // 3) stale content reconciles (the newest write wins) …
    if (this._config.Integrity?.ChecksumDb != false)
      this._integrity.QuickScan(this._Invalidate);

    // … 4) and owed duplication heals in the background (FR-HEAL: full health as fast as possible)
    this.RequestHeal();
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
    var jobs = new List<IBackgroundJob> { new OwedSyncJob(this, deferWindow, maxDefer), new DrainJob(this), new MemberWatchJob(this), new HealJob(this) };
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

    this._Invalidate(normalized);
    this._EnsureShadows(normalized, this._placement.ResolveCopies(normalized));
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

    // heal any stale journal mirror (a member that missed a compaction) BEFORE reading it for
    // recovery, so a long-completed intent can never be replayed as interrupted (SAFE-OFFLINE)
    this._journal.ReconcileMirrors();

    // recovery before serving: roll forward, reconcile, clean temps (FR-RECOVER)
    var report = new PoolRecovery([.. this._Online], this._journal).Run();
    if (report.AnythingDone) {
      DriveBender.Logger($"Recovery: {report.RolledForward} rolled forward, {report.Reconciled} reconciled, {report.TempsRemoved} staging files removed");
      this._activity.Publish(ActivityKind.Recovery, "", report.RolledForward + report.Reconciled, reason: "journal replay on mount");
    }

    // members that were offline while the pool last changed replay what they missed (SAFE-OFFLINE)
    var tombstonesReplayed = this._tombstones.Replay([.. this._Online], [.. this._members.Select(m => m.Io.MemberId)]);
    if (tombstonesReplayed > 0)
      DriveBender.Logger($"Applied {tombstonesReplayed} missed namespace change(s) from the tombstone log");

    // externally-modified members are caught before serving stale data (FR-OOB-MOUNT)
    if (this._config.Integrity?.ChecksumDb != false) {
      var oob = this._integrity.QuickScan(this._Invalidate);
      foreach (var issue in oob)
        DriveBender.Logger($"[Integrity]{issue.Kind}: {issue.Path} — {issue.Message}");
    }

    this._mountOptions = options;

    // any under-duplication (writes taken while a member was away, deferred shadow placement)
    // converges in the background without waiting for an explicit repair (FR-HEAL)
    this.RequestHeal();
  }

  public void Unmount() {
    if (this._mountOptions == null)
      return;

    // clean unmount: staged files publish and every owed copy applies before the mount releases (FR-CLEAN-UNMOUNT)
    foreach (var stagedPath in this._staging.Keys.ToArray())
      this._PublishStaged(stagedPath);
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
    var dataName = this._DataName(normalized);
    var copies = this._placement.ResolveCopies(dataName);
    if (copies.Count > 0)
      return copies[0].Volume.Stat(dataName, copies[0].Shadow);

    foreach (var member in this._Online)
      if (member.FolderExists(normalized, false))
        return new(0, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory);

    return null;
  }

  public void SetAttributes(string path, FileMetaPatch patch) {
    this._RequireWritable();
    var normalized = PoolPaths.Normalize(path);
    var dataName = this._DataName(normalized);
    var copies = this._placement.ResolveCopies(dataName);
    if (copies.Count == 0)
      throw new PoolFsException(PoolFsError.NotFound, $"Path not found: {path}");

    foreach (var copy in copies)
      copy.Volume.SetTimestamps(dataName, copy.Shadow, patch.CreationTimeUtc, patch.LastWriteTimeUtc);

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

    // files still being written are physically hidden temps — the listing shows them logically
    // with their live size (FR-STAGED-WRITE)
    var stagingPrefix = normalized.Length == 0 ? "" : normalized + "/";
    foreach (var stagedPath in this._staging.Keys) {
      if (!stagedPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase)
          || stagedPath.Length <= stagingPrefix.Length
          || stagedPath.IndexOf('/', stagingPrefix.Length) >= 0)
        continue;

      var name = stagedPath[stagingPrefix.Length..];
      if (entries.ContainsKey(name))
        continue;

      var meta = this._StatUncached(stagedPath);
      var length = this._writeBuffer.OverlayLength(stagedPath, meta?.Length ?? 0);
      var written = meta?.LastWriteTimeUtc ?? this._clock();
      entries[name] = new(name, NodeKind.File, length, written, written);
      folderSeen = true;
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

    // staged temp-name lifecycle needs an atomic rename on every member to publish; members
    // without it (whole-file remote backends) fall back to writing the final name in place
    var staged = this._Online.All(m => (m.Caps & BackendCaps.AtomicRename) != 0);
    var physical = staged ? _StagedNameOf(normalized) : normalized;

    var sequence = this._journal.LogIntent(JournalOp.Create, physical, memberId: target.MemberId);
    var parent = PoolPaths.GetParent(normalized);
    if (parent.Length > 0)
      target.EnsureFolder(parent, false);

    using (var stream = target.OpenWrite(physical, false, true))
      stream.Flush();

    this._integrity.RecordWholeFile(target, physical, false, []);
    this._EnsureShadows(physical, []);
    if (staged)
      this._staging[normalized] = sequence; // the Create intent stays open until the publish rename
    else
      this._journal.Complete(sequence, JournalOp.Create);

    this._Invalidate(normalized);
    this._Invalidate(physical);
    this._shadow.Record(normalized, new(NodeKind.File, 0, this._clock()));
    return this._handles.Open(normalized, AccessMode.ReadWrite).Handle;
  }

  /// <summary>
  /// Brings a file up to its folder's duplication level D by creating missing shadow copies
  /// (SAFE-DUP), streamed from an existing copy so the file is never held in RAM (SAFE-BIGFILE).
  /// </summary>
  private void _EnsureShadows(string normalized, IReadOnlyList<PhysicalCopy> knownCopies) {
    var duplication = this._placement.DuplicationLevelFor(PoolPaths.GetParent(normalized));
    if (duplication < 2)
      return;

    var holders = knownCopies.Count > 0
      ? knownCopies.Select(c => c.Volume).ToList()
      : [.. this._Online.Where(m => m.FileExists(normalized, false) || m.FileExists(normalized, true))];
    if (holders.Count == 0)
      return;

    var sourceVol = holders[0];
    var sourceShadow = knownCopies.Count > 0 ? knownCopies[0].Shadow : !sourceVol.FileExists(normalized, false);
    var size = sourceVol.Stat(normalized, sourceShadow)?.Length ?? 0;

    while (holders.Count < duplication) {
      var target = this._placement.ChooseShadowTarget(size, holders);
      if (target == null) {
        DriveBender.Logger($"[Warning]Duplication level {duplication} for '{normalized}' not placeable — no independent failure domain left; owed copies deferred (SAFE-PHYS)");
        return;
      }

      var parent = PoolPaths.GetParent(normalized);
      target.EnsureFolder(parent, true);
      WholeFilePublisher.CopyBetween(sourceVol, normalized, sourceShadow, target, normalized, true);
      this._activity.Publish(ActivityKind.Duplicate, normalized, size,
        fromMember: sourceVol.DisplayName, toMember: target.DisplayName,
        reason: $"duplication level {duplication}");
      holders.Add(target);
    }
  }

  public void Rename(string from, string to, RenameFlags flags) {
    this._RequireWritable();
    var fromNormalized = PoolPaths.Normalize(from);
    var toNormalized = PoolPaths.Normalize(to);

    // renaming a file that is still being written publishes it first (temp → final), then renames
    if (this._staging.ContainsKey(fromNormalized))
      this._PublishStaged(fromNormalized);

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

    this._RecordTombstoneForOffline(JournalOp.Rename, fromNormalized, toNormalized);
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

    // dirty children must land under the old name before the tree moves (SAFE-NOLOSS), and
    // children still being written publish first so no temp names travel with the subtree
    var fromPrefix = fromNormalized + "/";
    foreach (var stagedChild in this._staging.Keys.Where(k => k.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
      this._PublishStaged(stagedChild);
    foreach (var dirty in this._writeBuffer.DirtyPaths.Where(p => p.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
      this.FlushPath(dirty);

    this._RecordTombstoneForOffline(JournalOp.Rename, fromNormalized, toNormalized);
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

    // deleting a file that never finished writing: drop its temps — it never existed (FR-STAGED-WRITE)
    if (this._staging.TryRemove(normalized, out var createSequence)) {
      var stagedName = _StagedNameOf(normalized);
      var discardedStaged = this._writeBuffer.Drain(normalized); // buffered blocks are moot
      foreach (var member in this._Online)
      foreach (var shadow in new[] { false, true })
        if (member.FileExists(stagedName, shadow))
          member.Delete(stagedName, shadow);

      // complete the Create intent AND every owed-write intent the buffer held — otherwise they
      // linger open forever and are replayed (noisily) at every subsequent mount
      this._journal.Complete(createSequence, JournalOp.Create);
      if (discardedStaged != null)
        foreach (var staleSequence in discardedStaged.Value.journalSequences)
          this._journal.Complete(staleSequence, JournalOp.Write);

      this._integrity.InvalidateFile(stagedName);
      this._Invalidate(stagedName);
      this._Invalidate(normalized);
      this._shadow.Remove(normalized);
      return;
    }

    var copies = this._placement.ResolveCopies(normalized);
    if (copies.Count == 0)
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {path}");

    // pending buffered mutations are moot once the file dies; their intents complete with the delete
    var discarded = this._writeBuffer.Drain(normalized);

    // offline members keep their stale copies — record what they missed so no ghost resurrects
    this._RecordTombstoneForOffline(JournalOp.Delete, normalized);

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

    this._RecordTombstoneForOffline(JournalOp.RemoveDir, normalized);
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

  /// <summary>
  /// Records a namespace change for every member that is offline right now (SAFE-OFFLINE):
  /// without it, a returning member would resurrect deleted/renamed files into the pool.
  /// Called BEFORE the mutation so a crash in between merely replays a no-op.
  /// </summary>
  private void _RecordTombstoneForOffline(JournalOp op, string path, string? targetPath = null) {
    var offline = this._members.Where(m => !m.Io.IsOnline).Select(m => m.Io.MemberId).ToArray();
    if (offline.Length > 0)
      this._tombstones.Record(op, path, targetPath, offline);
  }

  #endregion

  #region data

  public NodeHandle Open(string path, AccessMode mode, ShareMode share) {
    this._RequireMounted();
    if ((mode & AccessMode.Write) != 0)
      this._RequireWritable();

    var normalized = PoolPaths.Normalize(path);
    if (this._placement.ResolveCopies(this._DataName(normalized)).Count == 0)
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
      var dataPath = this._DataName(path); // a staging file reads from its temp physical
      var copies = this._placement.ResolveCopies(dataPath);
      if (copies.Count == 0)
        throw new PoolFsException(PoolFsError.NotFound, $"File vanished: {path}");

      var length = this._writeBuffer.OverlayLength(path, _StatAnyCopy(copies, dataPath)?.Length ?? 0);
      if (offset >= length)
        return 0; // reads past EOF return 0 bytes (FR-READ)

      var count = (int)Math.Min(buffer.Length, length - offset);
      this._ReadRange(path, dataPath, copies, buffer[..count], offset, count >= this._mirrorSplitThreshold);
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

        // background prefetch (FR-RA): the window loads on the thread pool so the foreground
        // read returns at once; one prefetch chain per path at a time prevents pile-up
        if (prefetchBytes > 0 && this._prefetching.TryAdd(path, 0)) {
          var from = offset + count;
          ThreadPool.QueueUserWorkItem(_ => {
            try {
              this._Prefetch(dataPath, copies, from, prefetchBytes, length);
            } catch (Exception) {
              // prefetch is strictly best-effort — the foreground read surfaces real errors
            } finally {
              this._prefetching.TryRemove(path, out var _);
            }
          });
        }
      }

      return count;
    } finally {
      open.File.Lock.ExitReadLock();
    }
  }

  private void _ReadRange(string path, string dataPath, IReadOnlyList<PhysicalCopy> copies, Span<byte> buffer, long offset, bool mirrorSplit) {
    var blockSize = this._cache.Pages.BlockSize;

    // FR-MIRROR: a large read pulls its uncached blocks from MULTIPLE copies CONCURRENTLY — each
    // copy serves different offsets, so independent disks add up their throughput
    if (mirrorSplit && copies.Count > 1) {
      var missing = new List<long>();
      for (var blockIndex = offset / blockSize; blockIndex <= (offset + buffer.Length - 1) / blockSize; ++blockIndex)
        if (!this._cache.Pages.TryGet(new(this._poolId, dataPath, blockIndex), out _))
          missing.Add(blockIndex);

      if (missing.Count > 1)
        try {
          Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = copies.Count },
            blockIndex => this._LoadBlock(dataPath, copies, blockIndex, mirrorSplit: true));
        } catch (AggregateException e) when (e.InnerExceptions.OfType<PoolFsException>().FirstOrDefault() is { } inner) {
          throw inner;
        }
    }

    var written = 0;
    while (written < buffer.Length) {
      var absolute = offset + written;
      var blockIndex = absolute / blockSize;
      var blockOffset = (int)(absolute % blockSize);
      // the overlay (dirty write buffer) is keyed by the LOGICAL name; disk blocks by the physical one
      var block = this._writeBuffer.OverlayBlock(path, blockIndex, blockSize, this._LoadBlock(dataPath, copies, blockIndex, mirrorSplit));
      var available = Math.Min(buffer.Length - written, block.Length - blockOffset);
      if (available <= 0)
        throw new PoolFsException(PoolFsError.IoError, $"Short read at block {blockIndex} of '{path}'");

      block.AsSpan(blockOffset, available).CopyTo(buffer[written..]);
      written += available;
    }
  }

  private byte[] _LoadBlock(string path, IReadOnlyList<PhysicalCopy> copies, long blockIndex, bool mirrorSplit, long? guardEpoch = null) {
    var key = new PageKey(this._poolId, path, blockIndex);
    if (this._cache.Pages.TryGet(key, out var cached))
      return cached;

    // readiness block routing (FR-MIRROR, FR-STRIPE-READY): a split read sends each block to the
    // copy that is READY — least outstanding I/O, then measured latency, plain alternation when
    // idle — so a fast storage serves more blocks than a slow one; when the routed copy fails
    // mid-read (dying disk, vanished member) every other copy is tried before the read may fail
    var rotation = mirrorSplit && copies.Count > 1 ? (int)(blockIndex % copies.Count) : 0;
    var order = Enumerable.Range(0, copies.Count)
      .OrderBy(i => mirrorSplit && copies.Count > 1 ? this._LoadScore(copies[i].Volume) : 0)
      .ThenBy(i => (i - rotation + copies.Count) % copies.Count)
      .ToArray();
    PoolFsException? lastError = null;
    foreach (var index in order) {
      var copy = copies[index];
      byte[] block;
      this._BeginIo(copy.Volume.MemberId);
      try {
        block = _ReadBlockFrom(copy, path, blockIndex, this._cache.Pages.BlockSize);
      } catch (PoolFsException e) {
        lastError = e;
        continue;
      } finally {
        this._EndIo(copy.Volume.MemberId);
      }

      if (lastError != null)
        this._activity.Publish(ActivityKind.Recovery, path, block.Length, fromMember: copy.Volume.DisplayName,
          reason: "read failover — a copy failed mid-read, another one served the block");

      // a lock-free prefetch guards its Put with the epoch it captured before reading, so a write
      // that invalidated the path in the meantime rejects this now-stale block (SAFE-COHERE)
      if (guardEpoch is { } epoch)
        this._cache.Pages.PutIfCurrent(key, block, epoch);
      else
        this._cache.Pages.Put(key, block);
      return block;
    }

    throw lastError ?? new PoolFsException(PoolFsError.IoError, $"No copy of '{path}' could be read");
  }

  /// <summary>Stat with failover: any surviving copy answers when the first one's storage just died.</summary>
  private static FileMeta? _StatAnyCopy(IReadOnlyList<PhysicalCopy> copies, string path) {
    PoolFsException? lastError = null;
    foreach (var copy in copies)
      try {
        return copy.Volume.Stat(path, copy.Shadow);
      } catch (PoolFsException e) {
        lastError = e;
      }

    throw lastError ?? new PoolFsException(PoolFsError.NotFound, $"File not found: {path}");
  }

  private static byte[] _ReadBlockFrom(PhysicalCopy copy, string path, long blockIndex, int blockSize) {
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

    return block;
  }

  private void _Prefetch(string path, IReadOnlyList<PhysicalCopy> copies, long fromOffset, long windowBytes, long fileLength) {
    var blockSize = this._cache.Pages.BlockSize;
    var lastByte = Math.Min(fileLength, fromOffset + windowBytes) - 1; // never past EOF (FR-RA)
    var missing = new List<long>();
    for (var blockIndex = fromOffset / blockSize; blockIndex <= lastByte / blockSize; ++blockIndex)
      if (!this._cache.Pages.TryGet(new(this._poolId, path, blockIndex), out _))
        missing.Add(blockIndex);

    if (missing.Count == 0)
      return;

    // capture the invalidation epoch up front: any write that lands while this prefetch is in
    // flight bumps it, so the guarded Put below drops the pre-write block instead of poisoning
    var epoch = this._cache.Pages.EpochOf(this._poolId);

    // the whole window loads CONCURRENTLY across the copies (readiness-routed): fast storages keep
    // filling the cache while a slow one finishes its block, so once the slow block arrives a burst
    // of already-ready blocks hands over at once
    try {
      Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, copies.Count) },
        blockIndex => this._LoadBlock(path, copies, blockIndex, mirrorSplit: copies.Count > 1, guardEpoch: epoch));
    } catch (Exception) {
      // prefetch is best-effort; the foreground read will surface real errors
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
      var dataPath = this._DataName(path); // a staging file writes into its temp physical
      IReadOnlyList<PhysicalCopy> copies = this._placement.ResolveCopies(dataPath);
      if (copies.Count == 0)
        throw new PoolFsException(PoolFsError.NotFound, $"File vanished: {path}");

      if (mode == WriteMode.Append)
        offset = this._writeBuffer.OverlayLength(path, copies[0].Volume.Stat(dataPath, copies[0].Shadow)?.Length ?? 0);

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

      // FR-STRIPE-READY: when the ack needs fewer copies than exist, each block goes to the
      // storage that is READY — least outstanding I/O first, measured latency as the tiebreak,
      // plain rotation only when everything is idle and unmeasured. A fast SSD drains its queue
      // quicker and naturally takes more blocks than a slow/busy HDD; the owed-sync job copies
      // the missing blocks between storages in the background until all share the full file
      if (eligibleCount > 1 && requiredCopies < eligibleCount) {
        var block = offset / Math.Max(1, this._cache.Pages.BlockSize);
        var eligible = orderedCopies.Take(eligibleCount).ToArray();
        var rotation = (int)(block % eligible.Length);
        copies = [
          .. eligible
            .Select((copy, index) => (copy, index))
            .OrderBy(t => this._LoadScore(t.copy.Volume))
            .ThenBy(t => (t.index - rotation + eligible.Length) % eligible.Length)
            .Select(t => t.copy),
          .. orderedCopies.Skip(eligibleCount),
        ];
      }

      // intent → mutate the ack set durably → (write-through: complete now; write-back /
      // deferred: the intent stays open until the owed copies are applied, so a crash
      // in the gap reconciles from the durable primary) (SAFE-ORDER, FR-WT, FR-WB)
      var sequence = this._journal.LogIntent(JournalOp.Write, dataPath, offset: offset, length: bytes.Length);
      var mirroredNow = requiredCopies > 1;

      // ack quorum with mid-write failover (SAFE-NOLOSS): a storage vanishing DURING the write
      // does not fail the driver — the block redirects to the next ready storage, and the failed
      // member's copy stays owed, served later from the write-back cache, never re-read from the
      // broken storage
      var appliedFlags = new bool[copies.Count];
      this._WriteAckQuorum(copies, requiredCopies, dataPath, bytes, offset, appliedFlags);
      var appliedCount = appliedFlags.Count(f => f);

      if (appliedCount >= copies.Count) {
        // every copy now durably holds these bytes: any older buffered write of this range is
        // obsolete and must not later flush over them (SAFE-NOLOSS)
        this._writeBuffer.Supersede(path, offset, bytes.Length);
        this._journal.Complete(sequence, JournalOp.Write);
      }
      else if (policy == WritePolicy.WriteThrough) {
        // write-through: apply the remaining copies now (best effort); a copy that fails stays
        // owed under the open intent so recovery reconciles it (SAFE-REMOTE)
        mirroredNow = true;
        if (this._TryWriteRemaining(copies, appliedFlags, dataPath, bytes, offset)) {
          this._writeBuffer.Supersede(path, offset, bytes.Length);
          this._journal.Complete(sequence, JournalOp.Write);
        } else
          this._StageWithThrottle(path, offset, bytes, sequence, appliedCount); // hold the block for the lagging copy
      }
      else if (!this._StageWithThrottle(path, offset, bytes, sequence, appliedCount)) {
        // buffer full even after throttling: degrade to synchronous catch-up (FR-BACKP)
        mirroredNow = true;
        if (this._TryWriteRemaining(copies, appliedFlags, dataPath, bytes, offset)) {
          this._writeBuffer.Supersede(path, offset, bytes.Length);
          this._journal.Complete(sequence, JournalOp.Write);
        }
        // else: the intent stays open — recovery reconciles the copies that never took the block
      }

      this._activity.Publish(ActivityKind.Write, path, bytes.Length, toMember: copies.Count > 0 ? copies[0].Volume.DisplayName : null, reason: policy.ToString());
      // the mirrored copy is its own visible movement (FR-UI-MAP: the duplicate leg to the second member)
      if (mirroredNow && copies.Count > 1)
        this._activity.Publish(ActivityKind.Duplicate, path, bytes.Length, fromMember: copies[0].Volume.DisplayName, toMember: copies[1].Volume.DisplayName, reason: "mirrored write");

      // coherency: a read after this write must return the new bytes (SAFE-COHERE)
      this._integrity.InvalidateFile(dataPath);
      this._cache.Pages.InvalidatePath(this._poolId, dataPath);
      this._cache.Metadata.InvalidatePath(this._poolId, path);
      return bytes.Length;
    } finally {
      open.File.Lock.ExitWriteLock();
    }
  }

  private void _WriteOneCopy(PhysicalCopy copy, string path, byte[] bytes, long offset) {
    this._BeginIo(copy.Volume.MemberId); // visible to the readiness selector while queued
    try {
      using var stream = copy.Volume.OpenWrite(path, copy.Shadow, false);
      stream.Seek(offset, SeekOrigin.Begin);
      stream.Write(bytes, 0, bytes.Length);
      stream.Flush(); // durability barrier per copy (SAFE-FSYNC)
    } finally {
      this._EndIo(copy.Volume.MemberId);
    }
  }

  /// <summary>
  /// Lands the block on enough storages to satisfy the ack quorum: the preferred (ready-first)
  /// set writes in parallel; a member failing mid-write is substituted by the next copy holder
  /// so the driver's write still succeeds — its owed copy converges from the write cache later.
  /// Throws only when the quorum itself is unreachable.
  /// </summary>
  private void _WriteAckQuorum(IReadOnlyList<PhysicalCopy> copies, int requiredCopies, string path, byte[] bytes, long offset, bool[] appliedFlags) {
    var target = Math.Min(requiredCopies, copies.Count);
    var errors = new Exception?[copies.Count];
    Parallel.For(0, target, i => {
      try {
        this._WriteOneCopy(copies[i], path, bytes, offset);
        appliedFlags[i] = true;
      } catch (Exception e) {
        errors[i] = e;
      }
    });

    PoolFsException? lastError = null;
    foreach (var error in errors.Where(e => e != null))
      if (error is PoolFsException poolError)
        lastError = poolError;
      else
        throw error!;

    var succeeded = appliedFlags.Count(f => f);
    for (var i = target; i < copies.Count && succeeded < target; ++i)
      try {
        this._WriteOneCopy(copies[i], path, bytes, offset);
        appliedFlags[i] = true;
        ++succeeded;
        this._activity.Publish(ActivityKind.Recovery, path, bytes.Length, toMember: copies[i].Volume.DisplayName,
          reason: "block redirected — a storage failed mid-write; its copy stays owed from the write cache");
      } catch (PoolFsException e) {
        lastError = e;
      }

    if (succeeded < target)
      throw lastError ?? new PoolFsException(PoolFsError.IoError, $"No storage accepted the block of '{path}'");
  }

  /// <summary>Best-effort application to every copy not yet holding the block; true = all copies have it now.</summary>
  private bool _TryWriteRemaining(IReadOnlyList<PhysicalCopy> copies, bool[] appliedFlags, string path, byte[] bytes, long offset) {
    Parallel.For(0, copies.Count, i => {
      if (appliedFlags[i])
        return;

      try {
        this._WriteOneCopy(copies[i], path, bytes, offset);
        appliedFlags[i] = true;
      } catch (Exception) {
        // stays owed — the open journal intent covers it
      }
    });

    return appliedFlags.All(f => f);
  }

  /// <summary>
  /// FR-BACKP as a THROTTLE: the write cache must never drop a block that has not reached all
  /// available storages. When the budget is exhausted, THIS writer blocks while the oldest dirty
  /// files flush down to their storages, then retries — new blocks are only accepted once the
  /// evicted ones are safely written.
  /// </summary>
  private bool _StageWithThrottle(string path, long offset, byte[] bytes, long sequence, int durableCopies) {
    if (this._writeBuffer.StageWrite(path, offset, bytes, sequence, durableCopies))
      return true;

    foreach (var dirty in this._writeBuffer.DirtyPaths) {
      if (string.Equals(dirty, path, StringComparison.OrdinalIgnoreCase))
        continue;

      this.FlushPath(dirty); // blocking the writer IS the throttle
      if (this._writeBuffer.StageWrite(path, offset, bytes, sequence, durableCopies))
        return true;
    }

    return this._writeBuffer.StageWrite(path, offset, bytes, sequence, durableCopies);
  }

  /// <summary>
  /// Refuses an ack when fewer copies are reachable than the folder's effective
  /// minCopiesBeforeAck (SAFE-LZ) — UNLESS degraded writes are accepted (the default,
  /// §10 SAFE-DEGRADE): one lost drive must not turn into a write outage while at least one
  /// durable copy is reachable. The shortfall stays owed and heals when the member returns.
  /// </summary>
  private void _RequireAckQuorum(string path, int reachableCopies) {
    var effective = ConfigResolver.ResolveForFolder(this._config, PoolPaths.GetParent(path));
    var required = ConfigValidator.EffectiveMinCopiesBeforeAck(effective.Write, effective.Duplication);
    if (reachableCopies >= required)
      return;

    // degrade ONLY for a transient shortfall (a member is actually missing) — a structural one
    // (e.g. an undurable remote backend that can never satisfy the quorum, SAFE-REMOTE) keeps
    // refusing: it would otherwise silently weaken durability forever
    var memberMissing = this._members.Any(m => !m.Io.IsOnline);
    if (memberMissing && reachableCopies >= 1 && (this._config.Resilience?.AcceptDegradedWrites ?? true)) {
      if (this._degradedAckWarned.TryAdd(path, 0)) {
        DriveBender.Logger($"[Warning]Degraded write on '{path}': only {reachableCopies} of the required {required} copies are reachable — proceeding; the owed copies heal automatically");
        this._activity.Publish(ActivityKind.Recovery, path, reason: $"degraded write — {reachableCopies}/{required} copies reachable");
      }

      return;
    }

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
      var dataPath = this._DataName(path);
      var copies = this._placement.ResolveCopies(dataPath);
      if (copies.Count == 0)
        throw new PoolFsException(PoolFsError.NotFound, $"File vanished: {path}");

      var sequence = this._journal.LogIntent(JournalOp.Truncate, dataPath, length: length);
      foreach (var copy in copies)
        copy.Volume.Truncate(dataPath, copy.Shadow, length); // grows zero-filled or shrinks on all copies (FR-TRUNC)

      this._journal.Complete(sequence, JournalOp.Truncate);
      this._integrity.InvalidateFile(dataPath);
      this._cache.Pages.InvalidatePath(this._poolId, dataPath);
      this._cache.Metadata.InvalidatePath(this._poolId, path);
    } finally {
      open.File.Lock.ExitWriteLock();
    }
  }

  public void Flush(NodeHandle handle) {
    this._RequireMounted();
    var open = this._handles.Get(handle);
    this.FlushPath(open.File.Path); // fsync is an absolute durability barrier in every mode (SAFE-FSYNC)

    // fsync promises the data survives a crash — a staged file publishes NOW (temp → final);
    // without it the temp would be swept on recovery and the promised data lost
    if (this._staging.ContainsKey(open.File.Path))
      this._PublishStaged(open.File.Path);
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
    var dataName = this._DataName(normalized); // a staging file's owed blocks land in its temp physical
    if (ops.Count > 0) {
      var copies = this._placement.ResolveCopies(dataName);
      var volatileSequence = journalSequences.Count == 0 && copies.Count > 0
        ? this._journal.LogIntent(JournalOp.Write, dataName)
        : 0;

      foreach (var copy in copies) {
        using var stream = copy.Volume.OpenWrite(dataName, copy.Shadow, false);
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

    this._integrity.InvalidateFile(dataName);
    this._cache.Pages.InvalidatePath(this._poolId, dataName);
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
        var before = landing.Stat(path, false);
        var size = before?.Length ?? 0;
        var target = this._placement.ChooseDrainTarget(size, holders.Where(h => h.MemberId != landing.MemberId));
        if (target == null)
          continue;

        var sequence = this._journal.LogIntent(JournalOp.Drain, path, memberId: target.MemberId);

        var parent = PoolPaths.GetParent(path);
        if (parent.Length > 0)
          target.EnsureFolder(parent, false);

        // streamed drain — the file is copied through a fixed buffer, never held in RAM (SAFE-BIGFILE)
        WholeFilePublisher.CopyBetween(landing, path, false, target, path, false);

        // TOCTOU guard (SAFE-NOLOSS): between the initial check and here, a foreground write could
        // have opened, rewritten and closed this file. If it is now open/dirty, or its size/mtime
        // changed, the copy we just made is stale — remove it and leave the landing original (the
        // authoritative new version) in place rather than deleting the only copy of fresh data.
        var after = landing.Stat(path, false);
        if (this._writeBuffer.IsDirty(path) || this._handles.IsOpen(path)
            || after is not { } stillThere || before is not { } was
            || stillThere.Length != was.Length || stillThere.LastWriteTimeUtc != was.LastWriteTimeUtc) {
          if (target.FileExists(path, false))
            target.Delete(path, false);
          this._journal.Complete(sequence, JournalOp.Drain);
          continue; // try again on a later pump once the file settles
        }

        landing.Delete(path, false); // free the fast tier only after the durable capacity copy exists
        this._journal.Complete(sequence, JournalOp.Drain);
        this._integrity.InvalidateFile(path);
        this._activity.Publish(ActivityKind.Drain, path, size, landing.DisplayName, target.DisplayName, "landing-zone drain");

        this._Invalidate(path);
        this._EnsureShadows(path, this._placement.ResolveCopies(path));
        this._Invalidate(path);
        DriveBender.Logger($" - Drained '{path}' from '{landing.DisplayName}' to '{target.DisplayName}' ({size} bytes)");
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

  /// <summary>
  /// Requests a full duplication heal (FR-HEAL): the background HealJob enumerates every
  /// logical file and restores missing primaries/shadows incrementally. Triggered on mount,
  /// on member return, and by explicit repair operations; cheap to request repeatedly.
  /// </summary>
  public void RequestHeal() => Interlocked.Exchange(ref this._healScanRequested, 1);

  /// <summary>True while a heal scan or queued heal work is outstanding (test/observability hook).</summary>
  public bool HealPending => this._healScanRequested != 0 || this._healScan != null || !this._healQueue.IsEmpty;

  /// <summary>
  /// One bounded unit of heal work, driven by the scheduler: drains one queued path, or
  /// advances the enumeration a chunk. Never runs concurrently with itself (single pump).
  /// </summary>
  public bool HealStep() {
    if (this._mountOptions == null || this.IsReadOnly)
      return false; // a read-only mount must not mutate members — an explicit repair op can

    if (this._healQueue.TryDequeue(out var path)) {
      this._HealOne(path);
      return true;
    }

    var scan = this._healScan;
    if (scan == null) {
      if (Interlocked.Exchange(ref this._healScanRequested, 0) == 0)
        return false;

      this._healScan = scan = this._AllLogicalFiles().GetEnumerator();
    }

    for (var enqueued = 0; enqueued < 64; ++enqueued) {
      if (!scan.MoveNext()) {
        this._healScan = null;
        return true;
      }

      this._healQueue.Enqueue(scan.Current);
    }

    return true;
  }

  /// <summary>
  /// Restores one file to its duplication level: promotes a surviving shadow when the primary
  /// is gone, then creates missing copies via temp + atomic rename under a journalled intent
  /// (SAFE-DUP). Active files are skipped — they converge through the write path.
  /// </summary>
  private void _HealOne(string normalized) {
    if (this._staging.ContainsKey(normalized) || this._writeBuffer.IsDirty(normalized) || this._handles.IsOpen(normalized))
      return;

    var copies = this._placement.ResolveCopies(normalized);
    if (copies.Count == 0)
      return;

    var duplication = this._placement.DuplicationLevelFor(PoolPaths.GetParent(normalized));
    var holders = copies.Select(c => c.Volume).Distinct().ToList();
    var hasPrimary = copies.Any(c => !c.Shadow);
    if (hasPrimary && holders.Count >= duplication)
      return;

    // a readable source copy, chosen by failover — never materialised in RAM (SAFE-BIGFILE)
    var source = copies.FirstOrDefault(c => _CanRead(c.Volume, normalized, c.Shadow));
    if (source == null)
      return;

    var size = _StatAnyCopy(copies, normalized)?.Length ?? 0;

    if (!hasPrimary) {
      // promote: the shadow's member gets a primary so the file survives shadow-container loss
      var survivor = copies[0];
      var sequence = this._journal.LogIntent(JournalOp.ShadowCreate, normalized, memberId: survivor.Volume.MemberId);
      WholeFilePublisher.CopyBetween(survivor.Volume, normalized, true, survivor.Volume, normalized, false);
      survivor.Volume.Delete(normalized, true);
      this._journal.Complete(sequence, JournalOp.ShadowCreate);
      this._activity.Publish(ActivityKind.Recovery, normalized, size, toMember: survivor.Volume.DisplayName, reason: "primary restored from surviving shadow");
      source = survivor with { Shadow = false };
    }

    while (holders.Count < duplication) {
      var target = this._placement.ChooseShadowTarget(size, holders);
      if (target == null)
        break; // not placeable right now (SAFE-PHYS) — a later heal converges

      var sequence = this._journal.LogIntent(JournalOp.ShadowCreate, normalized, memberId: target.MemberId);
      target.EnsureFolder(PoolPaths.GetParent(normalized), true);
      WholeFilePublisher.CopyBetween(source.Volume, normalized, source.Shadow, target, normalized, true);
      this._journal.Complete(sequence, JournalOp.ShadowCreate);
      this._activity.Publish(ActivityKind.Duplicate, normalized, size,
        fromMember: source.Volume.DisplayName, toMember: target.DisplayName,
        reason: $"healed to duplication level {duplication}");
      holders.Add(target);
    }

    this._Invalidate(normalized);
  }

  /// <summary>True when a copy is currently readable (member online and the file present) — a cheap failover probe.</summary>
  private static bool _CanRead(IVolumeIO volume, string normalized, bool shadow) {
    try {
      return volume.IsOnline && volume.FileExists(normalized, shadow);
    } catch (PoolFsException) {
      return false;
    }
  }

  /// <summary>Every logical file across all online members — primaries and shadow-only survivors alike.</summary>
  private IEnumerable<string> _AllLogicalFiles() {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var member in this._Online.ToArray()) {
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
          if (entry.IsDirectory) {
            stack.Push(childPath);
            continue;
          }

          if (seen.Add(childPath))
            yield return childPath;
        }

        VolumeEntry[] shadows;
        try {
          shadows = member.FolderExists(folder, true) ? [.. member.List(folder, true)] : [];
        } catch (PoolFsException) {
          shadows = [];
        }

        foreach (var entry in shadows) {
          if (entry.IsDirectory || PoolPaths.IsHiddenName(entry.Name))
            continue;

          var childPath = folder.Length == 0 ? entry.Name : $"{folder}/{entry.Name}";
          if (seen.Add(childPath))
            yield return childPath;
        }
      }
    }
  }

  public void Close(NodeHandle handle) {
    var open = this._handles.Get(handle);
    var path = open.File.Path;
    this._handles.Close(handle);

    // last handle gone: publish the staged temp to its final name — the atomic rename is the
    // LAST action before the Create journal intent completes (FR-STAGED-WRITE)
    if (this._staging.ContainsKey(path) && !this._handles.IsOpen(path))
      this._PublishStaged(path);
  }

  /// <summary>
  /// Publishes a staged file: flushes its buffered blocks into the temp physical, atomically
  /// renames temp → final on every copy, and only then completes the Create intent. Until this
  /// ran, the file never looked fully written on any physical disk.
  /// </summary>
  private void _PublishStaged(string normalized) {
    if (!this._staging.ContainsKey(normalized))
      return;

    this.FlushPath(normalized); // owed blocks land in the temp physical first (mapping still active)
    if (!this._staging.TryRemove(normalized, out var createSequence))
      return; // another thread published concurrently

    var stagedName = _StagedNameOf(normalized);
    var copies = this._placement.ResolveCopies(stagedName);
    foreach (var copy in copies)
      copy.Volume.AtomicReplace(stagedName, normalized, copy.Shadow);

    this._integrity.RenameFile(stagedName, normalized);
    this._journal.Complete(createSequence, JournalOp.Create);
    this._Invalidate(stagedName);
    this._Invalidate(normalized);
    this._cache.Pages.InvalidatePath(this._poolId, stagedName);
    this._activity.Publish(ActivityKind.Write, normalized, 0, reason: "staged file published (temp → final)");
  }

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
