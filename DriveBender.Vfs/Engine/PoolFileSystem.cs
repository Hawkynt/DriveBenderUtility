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
  // FR-RA, double-buffered: how many read-ahead chains one path may have running at once. With
  // ONE, the reader stalls at every window boundary — the chain that would fetch window N+1 is
  // not allowed to start until the chain fetching window N has finished, so the moment the
  // application catches up with the frontier it waits at device latency instead of cache latency.
  // Two means the next window is already loading while the current one is being consumed, which
  // is the whole point of a read-ahead. It is not more than two because the windows themselves
  // double as access stays sequential, so two chains already cover a long way ahead, and every
  // extra chain is another set of threads competing for the same device queue.
  private const int _MAX_PREFETCH_CHAINS = 2;
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _prefetching = new(PoolPaths.PathComparer);

  /// <summary>Claims one of this path's read-ahead slots; false when they are all in use.</summary>
  private bool _TryBeginPrefetch(string path) {
    while (true) {
      if (this._prefetching.TryAdd(path, 1))
        return true;

      if (!this._prefetching.TryGetValue(path, out var running))
        continue; // it was released between the two calls — try to claim it afresh

      if (running >= _MAX_PREFETCH_CHAINS)
        return false;

      if (this._prefetching.TryUpdate(path, running + 1, running))
        return true;
    }
  }

  private void _EndPrefetch(string path) {
    while (true) {
      if (!this._prefetching.TryGetValue(path, out var running))
        return;

      // the last one out REMOVES the key rather than leaving a zero behind: a mount that reads a
      // million files would otherwise accumulate a million entries that are never looked at again
      if (running <= 1) {
        if (((ICollection<KeyValuePair<string, int>>)this._prefetching).Remove(new(path, running)))
          return;

        continue; // the value changed under us — re-read and decide again
      }

      if (this._prefetching.TryUpdate(path, running - 1, running))
        return;
    }
  }

  // FR-STAGED-WRITE: a file between Create and its last Close lives under a temp physical name
  // (*.TEMP.$DRIVEBENDER, hidden on disk), so it never looks fully written until it is. The value
  // is the still-open Create journal sequence — the atomic temp→final rename is the LAST action
  // before that intent completes; a crash before it leaves only temps the recovery sweep removes.
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _staging = new(PoolPaths.PathComparer);

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
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _degradedAckWarned = new(PoolPaths.PathComparer);

  // FR-STRIPE-READY: outstanding I/O per member — the stripe selector routes each block to the
  // storage that is READY, so a fast/idle SSD naturally takes more blocks than a slow/busy HDD
  private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _memberLoad = new();

  private void _BeginIo(Guid memberId) => this._memberLoad.AddOrUpdate(memberId, 1, static (_, v) => v + 1);
  private void _EndIo(Guid memberId) => this._memberLoad.AddOrUpdate(memberId, 0, static (_, v) => Math.Max(0, v - 1));

  // SAFE-DEGRADE, latency side: a storage that just failed an operation is almost certainly
  // about to fail the next one, and on a real dying disk that failure arrives via a driver
  // timeout measured in SECONDS. Trying it first for every block of a large read turns one bad
  // member into a pool-wide stall even though a healthy copy sits right beside it. A member that
  // throws is therefore parked at the BACK of the readiness order for a short window — never
  // removed, because it may simply have hit one bad sector and failover must stay a fallback,
  // not a fork in the code.
  private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _memberFaultedTicks = new();
  private const long _FAULT_COOLDOWN_MS = 5_000;

  private void _NoteMemberFault(Guid memberId) => this._memberFaultedTicks[memberId] = Environment.TickCount64;

  private bool _IsCoolingDown(Guid memberId) {
    if (!this._memberFaultedTicks.TryGetValue(memberId, out var at))
      return false;

    if (Environment.TickCount64 - at < _FAULT_COOLDOWN_MS)
      return true;

    // Expired entries are PRUNED rather than left to age in place: the fast path below is
    // "nothing has ever failed", and a single transient error hours ago would otherwise cost
    // every later read a lookup per copy per block for the lifetime of the mount.
    this._memberFaultedTicks.TryRemove(memberId, out _);
    return false;
  }

  /// <summary>Readiness score: a recent failure sinks a member outright, then queued work, then measured latency — lower is readier.</summary>
  private double _LoadScore(IVolumeIO volume)
    => (this._IsCoolingDown(volume.MemberId) ? 1_000_000.0 : 0.0)
       + (this._memberLoad.TryGetValue(volume.MemberId, out var inflight) ? inflight : 0) * 1000.0
       + (volume is MeasuredVolumeIO { Samples: > 0 } measured ? measured.AverageLatencyMs : 0.0);
  private readonly ShadowNamespace _shadow = new();

  // FR-PAR / §6.4: how wide a request may fan out across the storages behind it, and how much
  // any one device is allowed to have outstanding at once (CFG.io.queueDepthPerVolume)
  private readonly VolumeQueues _queues;
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
      members.ToDictionary(m => m.Io.MemberId, m => m.Role),
      members.ToDictionary(m => m.Io.MemberId, m => m.ReserveBytes),
      this._LoadScore); // new files go where the least work is already queued

    this._queues = new(effectiveConfig, members.ToDictionary(m => m.Io.MemberId, m => m.Role));
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
    this._queues.UpdateConfig(newConfig);
    this._activity.Publish(ActivityKind.Recovery, "", reason: "config reloaded");
    DriveBender.Logger("Configuration reloaded live");
  }

  /// <summary>
  /// Applies changed member roles live (reconfigure storage without remount): new writes land
  /// per the new tier layout right away; already-placed data moves via the drainer/rebalancer.
  /// </summary>
  public void UpdateMemberRoles(IReadOnlyDictionary<Guid, MemberRole> roles) {
    this._placement.UpdateRoles(roles);
    this._queues.UpdateRoles(roles); // the role is one of the queue-depth keys
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
    this._memberFaultedTicks.TryRemove(member.MemberId, out _); // a member that came back is a candidate again

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
    var jobs = new List<IBackgroundJob> {
      new OwedSyncJob(this, deferWindow, maxDefer), new DrainJob(this), new MemberWatchJob(this), new HealJob(this),
      new TrimIdleResourcesJob([.. this._members.Select(m => m.Io)]),
    };

    // the configured scrub cadence had no job behind it — the setting existed and nothing ran it
    if (this._config.Integrity?.ChecksumDb != false) {
      var quick = ScrubCadence(this._config.Integrity?.ScrubberSchedule, TimeSpan.FromHours(24));
      var deep = ScrubCadence(this._config.Integrity?.DeepScrubSchedule, TimeSpan.Zero);
      if (quick > TimeSpan.Zero || deep > TimeSpan.Zero)
        jobs.Add(new ScrubJob(this, quick, deep, this._clock));
    }
    if (this._config.Trash?.Enabled == true)
      jobs.Add(new TrashMaintenanceJob(this));

    return new(jobs);
  }

  /// <summary>
  /// Turns a scrub schedule into an interval. Accepts a plain duration ("36h"), the
  /// <c>idle-</c> cadences the default configuration uses, and "off"/null for disabled.
  /// </summary>
  public static TimeSpan ScrubCadence(string? schedule, TimeSpan fallback) {
    if (schedule == null)
      return fallback;

    var text = schedule.Trim().ToLowerInvariant();
    if (text.Length == 0 || text == "off" || text == "never" || text == "manual")
      return TimeSpan.Zero;

    // "idle-weekly" and friends: the cadence is the period; "idle" is a hint about WHEN within it,
    // which the cooperative pump already honours by yielding to foreground work
    var period = text.StartsWith("idle-", StringComparison.Ordinal) ? text[5..] : text;
    return period switch {
      "hourly" => TimeSpan.FromHours(1),
      "daily" => TimeSpan.FromDays(1),
      "weekly" => TimeSpan.FromDays(7),
      "monthly" => TimeSpan.FromDays(30),
      _ => _TryDuration(period, fallback),
    };
  }

  private static TimeSpan _TryDuration(string text, TimeSpan fallback) {
    try {
      return DurationSpec.Parse(text);
    } catch (Exception) {
      DriveBender.Logger($"[Warning]Unrecognised scrub schedule '{text}' — using {fallback}");
      return fallback;
    }
  }

  /// <summary>The cheap integrity pass: only files whose metadata deviates, or whose checksum a write marked stale.</summary>
  public IReadOnlyList<IntegrityIssue> RunQuickScrub() => this._integrity.QuickScan(this._Invalidate);

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

    // A file's logical length comes from TWO reads — the durable stat of a physical copy plus
    // the write buffer's overlay of the bytes still owed to the others — and they must be
    // atomic against a flush. FlushPath moves bytes from the overlay onto the copies while
    // holding this path's write lease; a caller that took the pre-flush stat (the lagging copy,
    // still short) and then the post-flush overlay (now empty) reports a length SHORTER than an
    // acknowledged write, and caches it for the whole metadata TTL (SAFE-COHERE, SAFE-NOLOSS).
    // A shared lease costs a lock pair and keeps concurrent readers concurrent.
    using var lease = this._handles.AcquireRead(normalized);
    return this._GetAttributesLocked(normalized, path);
  }

  /// <summary>The stat itself; the caller holds at least a shared lease on the path.</summary>
  private FileMeta _GetAttributesLocked(string normalized, string path) {
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

    // logical size = one copy's size, never the sum of copies (FR-STAT). Statted with failover:
    // trusting copies[0] alone meant a single dying storage failed the whole stat even though
    // every other copy was readable.
    var dataName = this._DataName(normalized);
    var copies = this._placement.ResolveCopies(dataName);
    if (copies.Count > 0)
      return _StatAnyCopy(copies, dataName);

    foreach (var member in this._Online)
      if (member.FolderExists(normalized, false))
        return new(0, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory);

    return null;
  }

  public void SetAttributes(string path, FileMetaPatch patch) {
    this._RequireWritable();
    var normalized = PoolPaths.Normalize(path);
    using var lease = this._handles.AcquireWrite(normalized); // copies must not move under the stamp
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

    var entries = new Dictionary<string, DirEntry>(PoolPaths.PathComparer);
    var folderSeen = normalized.Length == 0;

    // Captured BEFORE the members are enumerated. A listing takes each entry's length from
    // whichever member enumerates it first — which may be the copy still lagging behind an
    // acknowledged write — and never consulted the write buffer, so a file with owed bytes
    // listed at its stale on-disk size. Every path that is dirty NOW is re-stated under its
    // own lease below; a path clean at this point has no owed bytes to miss, and one that goes
    // dirty later is a write concurrent with the listing, where any snapshot is legal.
    var dirtyAtStart = this._writeBuffer.DirtyPaths;

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
      if (!stagedPath.StartsWith(stagingPrefix, PoolPaths.PathComparison)
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

    // owed bytes made visible: each still-dirty child is re-stated with its lease held, so the
    // durable length and the overlay are read atomically against a flush (SAFE-COHERE)
    var childPrefix = normalized.Length == 0 ? "" : normalized + "/";
    foreach (var dirty in dirtyAtStart) {
      if (!dirty.StartsWith(childPrefix, PoolPaths.PathComparison))
        continue;

      var name = dirty[childPrefix.Length..];
      if (name.Length == 0 || name.IndexOf('/') >= 0 || !entries.TryGetValue(name, out var listed) || listed.Kind != NodeKind.File)
        continue;

      using var lease = this._handles.AcquireRead(dirty);
      var durable = this._StatUncached(dirty);
      if (durable is not { } meta)
        continue;

      entries[name] = listed with { Length = this._writeBuffer.OverlayLength(dirty, meta.Length) };
    }

    IReadOnlyList<DirEntry> result = [.. entries.Values.OrderBy(e => e.Name, PoolPaths.PathComparer)];
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

    // exclusive so two concurrent creates of one path cannot both take the "new file" branch
    // and race the staged-temp lifecycle; disposal is idempotent, so the early return below
    // may release it before calling back into a lock-taking public method
    using var lease = this._handles.AcquireWrite(normalized);

    var existing = this._placement.ResolveCopies(normalized);
    if (existing.Count > 0) {
      if ((flags & CreateFlags.Exclusive) != 0)
        throw new PoolFsException(PoolFsError.Exists, $"File already exists: {path}");

      var handleForExisting = this._handles.Open(normalized, AccessMode.ReadWrite).Handle;
      lease.Dispose(); // SetLength re-takes this very lock through the handle (NoRecursion)
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

    // renaming a path to itself (identical, or only a case change on a case-insensitive backend)
    // must NEVER go through the overwrite path — "the target" resolves to the source's own copies,
    // and deleting them would destroy the file. An exact no-op returns; a case change flips in place.
    var sameFile = fromNormalized.Equals(toNormalized, PoolPaths.PathComparison);
    if (fromNormalized.Equals(toNormalized, StringComparison.Ordinal))
      return; // pure no-op

    // Differing ONLY in case is the dangerous shape. Where the storage folds the two names into
    // one file, "the target" IS the source and the overwrite path below would delete the very file
    // being renamed. The engine's own comparison follows the PLATFORM, which is right for its
    // internal maps and not sufficient here: an NTFS volume or an SMB share mounted under Linux is
    // case-insensitive on a platform that is not, so the member is asked rather than assumed.
    var caseOnly = fromNormalized.Equals(toNormalized, StringComparison.OrdinalIgnoreCase);

    // exclusive on BOTH endpoints for the whole rename, acquired in a deterministic ordinal
    // order so two renames in opposite directions can never deadlock. A case-only rename maps
    // to ONE state (the table is case-insensitive) and therefore takes a single lease.
    var takeFirst = string.CompareOrdinal(fromNormalized, toNormalized) <= 0 ? fromNormalized : toNormalized;
    var takeSecond = takeFirst == fromNormalized ? toNormalized : fromNormalized;
    using var leaseFirst = this._handles.AcquireWrite(takeFirst);
    using var leaseSecond = sameFile ? null : this._handles.AcquireWrite(takeSecond);

    // renaming a file that is still being written publishes it first (temp → final), then renames
    if (this._staging.ContainsKey(fromNormalized))
      this._PublishStagedLocked(fromNormalized);

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

    // a case-only rename has no distinct target to conflict with or overwrite — its "target
    // copies" ARE the source; only a genuinely different path is a real target
    var targetCopies = sameFile ? [] : this._placement.ResolveCopies(toNormalized);
    if (targetCopies.Count > 0 && (flags & RenameFlags.ReplaceExisting) == 0)
      throw new PoolFsException(PoolFsError.Exists, $"Target already exists: {to}");

    this._FlushPathLocked(fromNormalized); // pending mutations land under the old name first

    this._RecordTombstoneForOffline(JournalOp.Rename, fromNormalized, toNormalized);
    var sequence = this._journal.LogIntent(JournalOp.Rename, fromNormalized, toNormalized);

    // overwrite-on-rename removes every copy of the old target first (no orphans) — except where
    // that copy is the SOURCE wearing the other spelling, which is what a case-only rename means on
    // a member whose storage does not distinguish the two. Deleting it there destroys the file the
    // rename was supposed to move, and the AtomicReplace below flips the name in place anyway.
    foreach (var stale in targetCopies) {
      if (caseOnly && !stale.Volume.IsCaseSensitive)
        continue;

      stale.Volume.Delete(toNormalized, stale.Shadow);
    }

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
    if ((toNormalized + "/").StartsWith(fromNormalized + "/", PoolPaths.PathComparison))
      throw new PoolFsException(PoolFsError.InvalidArgument, $"Cannot move a folder into itself: {fromNormalized} → {toNormalized}");
    if (!this._ParentExists(toNormalized))
      throw new PoolFsException(PoolFsError.NotFound, $"Target parent folder not found: {toNormalized}");
    if (this._Online.Any(m => m.FolderExists(toNormalized, false) || m.FileExists(toNormalized, false)))
      throw new PoolFsException(PoolFsError.Exists, $"Target already exists: {toNormalized}");

    // dirty children must land under the old name before the tree moves (SAFE-NOLOSS), and
    // children still being written publish first so no temp names travel with the subtree
    var fromPrefix = fromNormalized + "/";
    foreach (var stagedChild in this._staging.Keys.Where(k => k.StartsWith(fromPrefix, PoolPaths.PathComparison)).ToArray())
      this._PublishStaged(stagedChild);
    foreach (var dirty in this._writeBuffer.DirtyPaths.Where(p => p.StartsWith(fromPrefix, PoolPaths.PathComparison)).ToArray())
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

    // exclusive for the whole delete: a background flush/heal must not be mid-way through
    // rewriting copies we are about to remove, and a reader must not observe a half-deleted set
    using var lease = this._handles.AcquireWrite(normalized);

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

    // Every primary and shadow copy goes — no orphans remain (FR-DELETE). A member that fails
    // DURING the delete is tombstoned rather than skipped: _RecordTombstoneForOffline above only
    // covers members already offline when the delete started, so a member that dropped out a
    // moment later would keep its copy and resurrect the file on its return (SAFE-OFFLINE).
    var missed = new List<Guid>();
    var deletedSomewhere = false;
    var attempted = 0;
    foreach (var member in this._Online.ToArray()) {
      ++attempted;
      try {
        foreach (var shadow in new[] { false, true })
          if (member.FileExists(normalized, shadow)) {
            member.Delete(normalized, shadow);
            deletedSomewhere = true;
          }
      } catch (PoolFsException) {
        missed.Add(member.MemberId);
      }
    }

    // nothing at all could be removed: this is a failed delete, not a degraded one
    if (attempted > 0 && !deletedSomewhere && missed.Count == attempted)
      throw new PoolFsException(PoolFsError.IoError, $"No member could delete '{normalized}'");

    if (missed.Count > 0) {
      this._tombstones.Record(JournalOp.Delete, normalized, null, [.. missed]);
      DriveBender.Logger($"[Warning]'{normalized}' could not be removed on {missed.Count} member(s) — recorded for replay when they return, so it cannot resurrect");
    }

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

    // Locked BY PATH, not by the state captured when the handle was opened: a replacing rename
    // repoints a path at a different state, and a handle that kept locking its original one would
    // exclude nobody — two writers on one path through two locks, which reads back as a torn file.
    // The lease resolves whichever state the path means right now (SAFE-COHERE).
    using var lease = this._handles.AcquireRead(open.File.Path);
    try {
      var path = lease.File.Path;
      var dataPath = this._DataName(path); // a staging file reads from its temp physical
      var copies = this._placement.ResolveCopies(dataPath);
      if (copies.Count == 0)
        throw new PoolFsException(PoolFsError.NotFound, $"File vanished: {path}");

      // the SAME resolved length GetAttributes reports — cross-checked across copies and served
      // from the metadata cache, so a sequential read no longer pays a stat syscall per call.
      // Safe to use the locked form: this handle's read lock IS the lock a path lease takes.
      var length = this._GetAttributesLocked(path, path).Length;
      if (offset >= length)
        return 0; // reads past EOF return 0 bytes (FR-READ)

      var count = (int)Math.Min(buffer.Length, length - offset);

      // A block shorter than the file's length says the copy that served it is BEHIND — unless
      // the write buffer is legitimately holding the rest, in which case a short disk block is
      // exactly right and the overlay supplies the tail. IsDirty is stable here: a flush takes
      // this path's write lease and we hold its read lock.
      var durableExpected = !this._writeBuffer.IsDirty(path);
      this._ReadRange(path, dataPath, copies, buffer[..count], offset, count >= this._mirrorSplitThreshold,
        durableExpected ? length : 0);
      this._activity.Publish(ActivityKind.Read, path, count, fromMember: copies[0].Volume.DisplayName, reason: "user I/O");

      if (this._readAheadEnabled) {
        // No lock: the map is shared by every handle on this file, so taking one here serialised
        // all of its readers behind a single monitor on the hottest path there is. The state itself
        // is per HANDLE, so the lock below contends only with the same caller's own reads.
        var state = lease.File.ReadAhead.GetOrAdd(handle.Value,
          _ => new ReadAheadState(this._readAheadMin, this._readAheadMax, this._readAheadAdaptive));

        long prefetchBytes;
        lock (state)
          prefetchBytes = state.OnRead(offset, count);

        // background prefetch (FR-RA): the window loads on the thread pool so the foreground read
        // returns at once. Up to _MAX_PREFETCH_CHAINS run at a time, so window N+1 is already on
        // its way while N is being consumed; a second chain that overlaps the first costs nothing
        // extra because _LoadBlock single-flights and _Prefetch skips blocks already in flight.
        if (prefetchBytes > 0 && this._TryBeginPrefetch(path)) {
          var from = offset + count;
          ThreadPool.QueueUserWorkItem(_ => {
            try {
              this._Prefetch(dataPath, copies, from, prefetchBytes, length, durableExpected ? length : 0);
            } catch (Exception) {
              // prefetch is strictly best-effort — the foreground read surfaces real errors
            } finally {
              this._EndPrefetch(path);
            }
          });
        }
      }

      return count;
    } finally {
      // the lease's own dispose releases the lock
    }
  }

  /// <summary>
  /// The order in which a block's copies are tried (FR-MIRROR, FR-STRIPE-READY): readiest first,
  /// rotation as the tiebreak. Run once PER BLOCK — a 1 GiB sequential read is thousands of calls
  /// — so the LINQ pipeline this replaces (Range → OrderBy → ThenBy → ToArray, an enumerator
  /// chain plus a sort buffer plus an array, every time) is now: nothing at all when there is one
  /// copy or no split, and a stable insertion sort over a handful of indices when there is.
  /// </summary>
  private int[] _OrderCopiesForBlock(IReadOnlyList<PhysicalCopy> copies, long blockIndex, bool mirrorSplit) {
    var count = copies.Count;
    if (count <= 1)
      return _IdentityOrder(count);

    // Without a split the rotation is skipped — mirrorReadSplitThreshold governs whether reads
    // are spread across copies, and this is not the place to overrule it. A member that just
    // FAILED still has to sink to the back, though, split or no split: otherwise a large read
    // tries the dying disk first for every one of its blocks and pays the driver timeout each
    // time, with a healthy copy sitting right beside it.
    if (!mirrorSplit && !this._AnyCoolingDown(copies))
      return _IdentityOrder(count);

    var order = new int[count];
    var scores = new double[count];
    var rotation = mirrorSplit ? (int)(blockIndex % count) : 0;
    for (var i = 0; i < count; ++i) {
      order[i] = (rotation + i) % count; // rotation order first, so a stable sort keeps it as the tiebreak
      scores[i] = this._LoadScore(copies[order[i]].Volume);
    }

    // insertion sort: stable by construction, and optimal at the sizes involved (a duplication level)
    for (var i = 1; i < count; ++i) {
      var index = order[i];
      var score = scores[i];
      var j = i - 1;
      while (j >= 0 && scores[j] > score) {
        order[j + 1] = order[j];
        scores[j + 1] = scores[j];
        --j;
      }

      order[j + 1] = index;
      scores[j + 1] = score;
    }

    return order;
  }

  /// <summary>True when any of these copies is inside its post-failure cooldown; checked before paying for a sort.</summary>
  private bool _AnyCoolingDown(IReadOnlyList<PhysicalCopy> copies) {
    if (this._memberFaultedTicks.IsEmpty)
      return false; // the overwhelmingly common case: nothing has ever failed

    for (var index = 0; index < copies.Count; ++index)
      if (this._IsCoolingDown(copies[index].Volume.MemberId))
        return true;

    return false;
  }

  /// <summary>Shared 0,1,2… index arrays for the no-split case, so the common read allocates nothing to order one copy.</summary>
  private static readonly int[][] _identityOrders = [.. Enumerable.Range(0, 9).Select(n => Enumerable.Range(0, n).ToArray())];

  private static int[] _IdentityOrder(int count)
    => count < _identityOrders.Length ? _identityOrders[count] : [.. Enumerable.Range(0, count)];

  /// <summary>
  /// Parallel options for work spread across <paramref name="copies"/>. When any copy parks its
  /// caller for a network round trip the work is routed onto the engine's own bounded threads, so
  /// a slow remote can never drain the shared thread pool and stall the rest of the process.
  /// </summary>
  private static ParallelOptions _ParallelOver(IReadOnlyList<PhysicalCopy> copies, int maxParallel) {
    var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallel) };
    for (var index = 0; index < copies.Count; ++index)
      if (copies[index].Volume.BlocksCallingThread) {
        options.TaskScheduler = BlockingIoScheduler.Shared;
        break;
      }

    return options;
  }

  /// <summary>
  /// Runs <paramref name="body"/> over [0, count) — INLINE when there is nothing to parallelise.
  /// Parallel.For pays for a partitioner, task scheduling and a join even for a single item, and
  /// the write path lands here on every write: with the common minCopiesBeforeAck of 1 the ack
  /// set is exactly one copy, so that whole apparatus was set up to schedule one call.
  /// </summary>
  private static void _ForEachIndex(int count, ParallelOptions options, Action<int> body) {
    if (count <= 0)
      return;

    if (count == 1 || options.MaxDegreeOfParallelism == 1) {
      for (var index = 0; index < count; ++index)
        body(index);

      return;
    }

    Parallel.For(0, count, options, body);
  }

  /// <summary>Bytes block <paramref name="blockIndex"/> must contain for a file of <paramref name="fileLength"/> bytes; 0 means "unknown, accept anything".</summary>
  private static int _ExpectedBlockLength(long fileLength, long blockIndex, int blockSize)
    => fileLength <= 0 ? 0 : (int)Math.Clamp(fileLength - blockIndex * blockSize, 0, blockSize);

  private void _ReadRange(string path, string dataPath, IReadOnlyList<PhysicalCopy> copies, Span<byte> buffer, long offset, bool mirrorSplit, long fileLength) {
    var blockSize = this._cache.Pages.BlockSize;
    var firstBlock = offset / blockSize;
    var lastBlock = (offset + buffer.Length - 1) / blockSize;

    // FR-PAR / FR-MIRROR: every block this request still needs is loaded CONCURRENTLY — across
    // the copies when the file is duplicated, and across the device's own queue when it is not.
    //
    // This used to require BOTH a duplicated file AND a read over the mirror-split threshold,
    // which meant the ordinary case ran a storage at queue depth ONE: block, wait, block, wait.
    // A device that can serve a dozen requests at once was being asked for one, and the tier's
    // other storages sat idle while it did. The width comes from the queue depths of the DISTINCT
    // devices the copies live on, so several storages in one tier add up instead of taking turns,
    // and a spindle is still only asked for the two concurrent requests it actually wants.
    if (lastBlock > firstBlock) {
      List<long>? missing = null;
      for (var blockIndex = firstBlock; blockIndex <= lastBlock; ++blockIndex)
        if (!this._cache.Pages.TryGet(new(this._poolId, dataPath, blockIndex), out _))
          (missing ??= []).Add(blockIndex);

      if (missing is { Count: > 1 }) {
        // Only a SPLIT read is served by more than one storage, so only a split read gets the
        // summed width — otherwise the extra workers would just park on the one device's own cap.
        var width = mirrorSplit ? this._queues.FanOutFor(copies) : this._queues.FanOutFor(copies[0].Volume);
        var fanOut = Math.Min(missing.Count, width);
        if (fanOut > 1)
          try {
            Parallel.ForEach(missing, _ParallelOver(copies, fanOut),
              blockIndex => this._LoadBlock(dataPath, copies, blockIndex, mirrorSplit,
                expectedLength: _ExpectedBlockLength(fileLength, blockIndex, blockSize)));
          } catch (Exception) {
            // Warm-up, not the read itself. The assembly loop below re-runs _LoadBlock for every
            // block in order, so a genuine failure surfaces there — at the FIRST bad block, with
            // its own message, rather than at whichever of them happened to lose the race here.
          }
      }
    }

    var written = 0;
    while (written < buffer.Length) {
      var absolute = offset + written;
      var blockIndex = absolute / blockSize;
      var blockOffset = (int)(absolute % blockSize);
      // the overlay (dirty write buffer) is keyed by the LOGICAL name; disk blocks by the physical one
      var loaded = this._LoadBlock(dataPath, copies, blockIndex, mirrorSplit,
        expectedLength: _ExpectedBlockLength(fileLength, blockIndex, blockSize));
      var block = this._writeBuffer.OverlayBlock(path, blockIndex, blockSize, loaded);
      var available = Math.Min(buffer.Length - written, block.Length - blockOffset);
      if (available <= 0)
        throw new PoolFsException(PoolFsError.IoError, $"Short read at block {blockIndex} of '{path}'");

      block.AsSpan(blockOffset, available).CopyTo(buffer[written..]);
      written += available;
    }
  }

  /// <summary>
  /// One block load already under way, so that N callers wanting the same block cost ONE trip to
  /// the storage instead of N. Without this a reader that outran its own read-ahead re-read the
  /// very block the prefetch chain had in flight, and two read-ahead chains on one path
  /// duplicated wherever their windows overlapped — the same bytes fetched twice, which is exactly
  /// the overhead a read-ahead exists to remove.
  /// </summary>
  private sealed class BlockLoad(int expectedLength, long epoch) {
    public readonly int ExpectedLength = expectedLength;

    /// <summary>The page-cache epoch captured BEFORE the read began — see <see cref="_TryAdoptLoad"/>.</summary>
    public readonly long Epoch = epoch;

    public readonly TaskCompletionSource<byte[]> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
  }

  /// <summary>
  /// Block loads under way right now. A read-ahead consults it to SKIP blocks somebody is already
  /// fetching, and <see cref="_LoadBlock"/> to join one instead of starting a second.
  /// </summary>
  private readonly System.Collections.Concurrent.ConcurrentDictionary<PageKey, BlockLoad> _loadsInFlight = new();

  /// <summary>
  /// Waits for a load someone else started and decides whether its bytes are usable here.
  ///
  /// Two things make that a decision rather than a hand-over. A background read-ahead runs
  /// WITHOUT the path lease, so its bytes may predate a write that has since landed — the same
  /// hazard <see cref="PageCache.PutIfCurrent"/> exists for. The pool's invalidation epoch only
  /// moves forward, so finding it still equal to the value captured before the read began proves
  /// nothing invalidated anything in the whole interval, which is the one condition under which
  /// a lease-holding reader may take those bytes. It is a POOL-wide epoch, so an unrelated write
  /// costs us an adoption we could in principle have kept; being conservative in that direction
  /// is free, and being wrong in the other is a stale read.
  ///
  /// And a failure is never inherited: the leader may have been asked to validate the block
  /// against a different length, or simply have been unlucky in which copy it tried, so a caller
  /// whose own attempt might succeed must make it.
  /// </summary>
  private bool _TryAdoptLoad(BlockLoad running, out byte[] block) {
    block = [];
    byte[] result;
    try {
      result = running.Completion.Task.GetAwaiter().GetResult();
    } catch (Exception) {
      return false;
    }

    if (this._cache.Pages.EpochOf(this._poolId) != running.Epoch)
      return false;

    block = result;
    return true;
  }

  private byte[] _LoadBlock(string path, IReadOnlyList<PhysicalCopy> copies, long blockIndex, bool mirrorSplit, long? guardEpoch = null, int expectedLength = 0) {
    var key = new PageKey(this._poolId, path, blockIndex);

    // There are three ways to come by this block — it is cached, it is already in flight, or it is
    // ours to fetch — and they race with one another. Losing that race between two of the checks
    // is a reason to LOOK AGAIN, not a reason to fetch the block a second time, which is why this
    // is a short loop rather than a fall-through: a fall-through turns every lost race into
    // exactly the duplicate device read the whole mechanism exists to remove.
    for (var attempt = 0; attempt < 3; ++attempt) {
      if (this._cache.Pages.TryGet(key, out var cached))
        return cached;

      if (this._loadsInFlight.TryGetValue(key, out var running)) {
        // "Equivalent" is exact: the same expected length means the same validation, so the answer
        // means the same thing to both callers. Two callers with different expectations of how long
        // this block should be are asking different questions, and each has to ask its own.
        if (running.ExpectedLength != expectedLength)
          break;

        if (this._TryAdoptLoad(running, out var shared))
          return shared;

        break; // the leader failed, or its bytes are no longer current — fetch it properly
      }

      var load = new BlockLoad(expectedLength, guardEpoch ?? this._cache.Pages.EpochOf(this._poolId));
      if (!this._loadsInFlight.TryAdd(key, load))
        continue; // somebody registered between the two calls — look again

      try {
        var block = this._ReadBlockThroughCopies(path, copies, blockIndex, mirrorSplit, guardEpoch, expectedLength);
        load.Completion.TrySetResult(block);
        return block;
      } catch (Exception e) {
        load.Completion.TrySetException(e);

        // Observed HERE, because nobody else may ever look: a failed load with no follower leaves
        // a task holding an exception no one read, and those surface later as unobserved-exception
        // noise from the finalizer rather than where they happened. The caller below still gets it.
        _ = load.Completion.Task.Exception;
        throw;
      } finally {
        // AFTER the completion is set, never before: a caller that took the entry a moment ago is
        // waiting on it and must be handed the result rather than left on a task nobody completes
        this._loadsInFlight.TryRemove(key, out _);
      }
    }

    return this._ReadBlockThroughCopies(path, copies, blockIndex, mirrorSplit, guardEpoch, expectedLength);
  }

  private byte[] _ReadBlockThroughCopies(string path, IReadOnlyList<PhysicalCopy> copies, long blockIndex, bool mirrorSplit, long? guardEpoch, int expectedLength) {
    var key = new PageKey(this._poolId, path, blockIndex);

    // readiness block routing (FR-MIRROR, FR-STRIPE-READY): a split read sends each block to the
    // copy that is READY — least outstanding I/O, then measured latency, plain alternation when
    // idle — so a fast storage serves more blocks than a slow one; when the routed copy fails
    // mid-read (dying disk, vanished member) every other copy is tried before the read may fail
    var order = this._OrderCopiesForBlock(copies, blockIndex, mirrorSplit);
    PoolFsException? lastError = null;
    byte[]? bestShort = null;
    string? bestShortMember = null;
    foreach (var index in order) {
      var copy = copies[index];
      byte[] block;
      this._BeginIo(copy.Volume.MemberId);
      try {
        // admission on the DEVICE, not the member: the fan-out above may have several blocks of
        // this same request in flight, and other requests theirs, and a disk has one queue
        using var admission = this._queues.Enter(copy.Volume);
        block = _ReadBlockFrom(copy, path, blockIndex, this._cache.Pages.BlockSize);
      } catch (PoolFsException e) {
        lastError = e;
        this._NoteMemberFault(copy.Volume.MemberId); // the rest of this read routes around it
        continue;
      } finally {
        this._EndIo(copy.Volume.MemberId);
      }

      // A copy that hits EOF where the file's length says there is more data is BEHIND — it
      // missed a write while it was away, or a heal has not reached it yet. Serving that block
      // produces a short read; CACHING it hides the lag behind a cache hit for the whole TTL.
      // Fail over to another copy instead, and only fall back to the longest short answer when
      // no copy can do better (SAFE-COHERE).
      if (expectedLength > 0 && block.Length < expectedLength) {
        if (bestShort == null || block.Length > bestShort.Length) {
          bestShort = block;
          bestShortMember = copy.Volume.DisplayName;
        }

        lastError = new(PoolFsError.IoError,
          $"Copy of '{path}' on '{copy.Volume.DisplayName}' ends at block {blockIndex} byte {block.Length}, but the file is {expectedLength} bytes there");
        continue;
      }

      if (lastError != null)
        this._activity.Publish(ActivityKind.Recovery, path, block.Length, fromMember: copy.Volume.DisplayName,
          reason: "read failover — a copy was unreadable or behind, another one served the block");

      // a lock-free prefetch guards its Put with the epoch it captured before reading, so a write
      // that invalidated the path in the meantime rejects this now-stale block (SAFE-COHERE)
      if (guardEpoch is { } epoch)
        this._cache.Pages.PutIfCurrent(key, block, epoch);
      else
        this._cache.Pages.Put(key, block);
      return block;
    }

    // every copy fell short: the shortfall is real (or every holder is lagging identically).
    // Hand back the longest answer UNCACHED so the next read re-checks rather than inheriting it.
    if (bestShort != null) {
      DriveBender.Logger($"[Warning]Every copy of '{path}' is short at block {blockIndex} — serving the longest ({bestShort.Length} bytes) from '{bestShortMember}' without caching it");
      this._activity.Publish(ActivityKind.Recovery, path, bestShort.Length, fromMember: bestShortMember,
        reason: "every copy is behind at this block");
      return bestShort;
    }

    throw lastError ?? new PoolFsException(PoolFsError.IoError, $"No copy of '{path}' could be read");
  }

  /// <summary>
  /// The authoritative metadata across a file's copies. Taking <c>copies[0]</c> meant the file's
  /// LENGTH was whatever the first-resolved member happened to hold: a copy that missed a write
  /// reported the file short, and since every read clamps to that length, the tail was
  /// unreachable — a silent truncation with no error anywhere. The NEWEST copy by last-write
  /// time wins, the same rule crash-recovery resync converges every copy on; a tie is broken by
  /// length so a stat can never hide bytes that demonstrably exist. Unreadable copies are
  /// skipped, so one dying storage cannot fail a stat the others can answer.
  /// </summary>
  private static FileMeta? _StatAnyCopy(IReadOnlyList<PhysicalCopy> copies, string path) {
    PoolFsException? lastError = null;
    FileMeta? best = null;
    var answered = false;
    foreach (var copy in copies)
      try {
        var meta = copy.Volume.Stat(path, copy.Shadow);
        answered = true;
        if (meta is not { } candidate)
          continue;

        if (best is not { } current
            || candidate.LastWriteTimeUtc > current.LastWriteTimeUtc
            || (candidate.LastWriteTimeUtc == current.LastWriteTimeUtc && candidate.Length > current.Length))
          best = candidate;
      } catch (PoolFsException e) {
        lastError = e;
      }

    if (answered)
      return best;

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

  /// <param name="expectedFileLength">The DURABLE length used to spot a lagging copy, or 0 when the write buffer holds bytes the disk legitimately lacks.</param>
  private void _Prefetch(string path, IReadOnlyList<PhysicalCopy> copies, long fromOffset, long windowBytes, long fileLength, long expectedFileLength) {
    var blockSize = this._cache.Pages.BlockSize;
    var lastByte = Math.Min(fileLength, fromOffset + windowBytes) - 1; // never past EOF (FR-RA)
    // Blocks already IN FLIGHT are skipped as well as blocks already cached. With two read-ahead
    // chains running, their windows overlap wherever the second was triggered before the first
    // finished; joining the first chain's loads would be correct but would only park a thread
    // waiting for bytes that are already on their way. A read-ahead's job is the bytes NOBODY is
    // fetching yet.
    var missing = new List<long>();
    for (var blockIndex = fromOffset / blockSize; blockIndex <= lastByte / blockSize; ++blockIndex) {
      var key = new PageKey(this._poolId, path, blockIndex); // built once and asked twice
      if (!this._cache.Pages.TryGet(key, out _) && !this._loadsInFlight.ContainsKey(key))
        missing.Add(blockIndex);
    }

    if (missing.Count == 0)
      return;

    // capture the invalidation epoch up front: any write that lands while this prefetch is in
    // flight bumps it, so the guarded Put below drops the pre-write block instead of poisoning
    var epoch = this._cache.Pages.EpochOf(this._poolId);

    // the whole window loads CONCURRENTLY across the copies (readiness-routed): fast storages keep
    // filling the cache while a slow one finishes its block, so once the slow block arrives a burst
    // of already-ready blocks hands over at once.
    //
    // The width used to be the COPY COUNT, which on the ordinary unduplicated file is one — so the
    // read-ahead window, the one thing on the read path that exists purely to keep the device busy,
    // was itself issued one block at a time. It is now the summed queue depth of the devices behind
    // the copies, which is what actually keeps them all working (§6.4).
    try {
      Parallel.ForEach(missing, _ParallelOver(copies, Math.Min(missing.Count, this._queues.FanOutFor(copies))),
        blockIndex => this._LoadBlock(path, copies, blockIndex, mirrorSplit: copies.Count > 1, guardEpoch: epoch,
          expectedLength: _ExpectedBlockLength(expectedFileLength, blockIndex, blockSize)));
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

    // a private array the driver's span is copied into ONCE: it is handed to the copies, then
    // (if owed) to the write buffer, which takes ownership of it rather than cloning again
    var bytes = data.ToArray();
    using var lease = this._handles.AcquireWrite(open.File.Path); // by PATH — see Read
    try {
      var path = lease.File.Path;
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
      //
      // EXCEPT into a not-yet-published staging temp, which needs no intent of its own. That temp
      // is invisible in the namespace, and the Create intent that opened the staging is still open
      // — that open intent is precisely what tells recovery to sweep it. Replaying a write into a
      // file recovery discards wholesale cannot fix anything, so the barrier buys nothing at all.
      // Measured: a small file paid FOUR durability barriers and two of them were this pair.
      //
      // The intent is opened LAZILY below if a copy ends up owed, because the owed-copy machinery
      // keys off the sequence. Logging it after the mutation is sound HERE and only here: the
      // fsync-before-mutate ordering exists so an interrupted mutation of LIVE data is
      // recoverable, and nothing about this temp survives a crash to need recovering.
      var staged = this._staging.ContainsKey(path);
      var sequence = staged
        ? 0L
        : this._journal.LogIntent(JournalOp.Write, dataPath, offset: offset, length: bytes.Length);

      long OwedIntent() => sequence != 0
        ? sequence
        : sequence = this._journal.LogIntent(JournalOp.Write, dataPath, offset: offset, length: bytes.Length);

      void CompleteIfLogged() {
        if (sequence != 0)
          this._journal.Complete(sequence, JournalOp.Write);
      }

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
        CompleteIfLogged();
      }
      else if (policy == WritePolicy.WriteThrough) {
        // write-through: apply the remaining copies now (best effort); a copy that fails stays
        // owed under the open intent so recovery reconciles it (SAFE-REMOTE)
        mirroredNow = true;
        if (this._TryWriteRemaining(copies, appliedFlags, dataPath, bytes, offset)) {
          this._writeBuffer.Supersede(path, offset, bytes.Length);
          CompleteIfLogged();
        } else
          this._StageWithThrottle(path, offset, bytes, OwedIntent(), appliedCount); // hold the block for the lagging copy
      }
      else if (!this._StageWithThrottle(path, offset, bytes, OwedIntent(), appliedCount)) {
        // buffer full even after throttling: degrade to synchronous catch-up (FR-BACKP)
        mirroredNow = true;
        if (this._TryWriteRemaining(copies, appliedFlags, dataPath, bytes, offset)) {
          this._writeBuffer.Supersede(path, offset, bytes.Length);
          CompleteIfLogged(); // OwedIntent ran in the condition above, so there is always one here
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
      // the lease's own dispose releases the lock
    }
  }

  private void _WriteOneCopy(PhysicalCopy copy, string path, byte[] bytes, long offset) {
    this._BeginIo(copy.Volume.MemberId); // visible to the readiness selector while queued
    try {
      using var admission = this._queues.Enter(copy.Volume); // the device's queue, not the member's
      using var stream = copy.Volume.OpenWrite(path, copy.Shadow, false);
      stream.Seek(offset, SeekOrigin.Begin);
      stream.Write(bytes, 0, bytes.Length);
      stream.Flush(); // durability barrier per copy (SAFE-FSYNC)
    } catch (Exception) {
      this._NoteMemberFault(copy.Volume.MemberId); // the next block prefers a storage that still works
      throw;
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
    _ForEachIndex(target, _ParallelOver(copies, target), i => {
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
    // count the copies that actually still owe the block: parallelising over the whole copy list
    // scheduled a task per copy even when all but one were already applied
    var pending = 0;
    for (var i = 0; i < copies.Count; ++i)
      if (!appliedFlags[i])
        ++pending;

    _ForEachIndex(copies.Count, _ParallelOver(copies, pending), i => {
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
      if (string.Equals(dirty, path, PoolPaths.PathComparison))
        continue;

      // the caller already holds THIS path's write lock; a second writer throttling in the
      // opposite direction would deadlock on a blocking acquire, so a path we cannot take
      // immediately is simply skipped — the next candidate frees budget just as well
      using (var lease = this._handles.TryAcquireWrite(dirty, TimeSpan.FromMilliseconds(50))) {
        if (lease == null)
          continue;

        this._FlushPathLocked(dirty); // blocking the writer IS the throttle
      }

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

    using var lease = this._handles.AcquireWrite(open.File.Path); // by PATH — see Read
    try {
      var path = lease.File.Path;
      // already holding this file's lock through the handle — the locked core, never the
      // lease-taking shell, which would recurse on a NoRecursion lock
      this._FlushPathLocked(path); // pending buffered writes apply before the truncate so ordering stays linear
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
      // the lease's own dispose releases the lock
    }
  }

  public void Flush(NodeHandle handle) {
    this._RequireMounted();
    var open = this._handles.Get(handle);

    // one lease covers both steps: a publish that raced the flush could otherwise rename the
    // temp away between them and strand the just-flushed blocks
    using var lease = this._handles.AcquireWrite(open.File.Path);
    var path = lease.File.Path;
    this._FlushPathLocked(path); // fsync is an absolute durability barrier in every mode (SAFE-FSYNC)

    // fsync promises the data survives a crash — a staged file publishes NOW (temp → final);
    // without it the temp would be swept on recovery and the promised data lost
    if (this._staging.ContainsKey(path))
      this._PublishStagedLocked(path);
  }

  /// <summary>
  /// Applies every buffered mutation of a path durably to all its copies and completes
  /// the open journal intents. Idempotent; no-op for clean files.
  /// </summary>
  public void FlushPath(string path) {
    var normalized = PoolPaths.Normalize(path);
    // the owed-copy drain+apply must be ATOMIC against the read/write path: without this lease
    // the buffer could be drained (so the overlay stops covering the owed bytes) while a
    // concurrent read is routed to the copy that has not received them yet — serving pre-write
    // content — or an older op could land after a newer foreground write (SAFE-NOLOSS)
    using var lease = this._handles.AcquireWrite(normalized);
    this._FlushPathLocked(normalized);
  }

  /// <summary>The flush itself; the caller holds this path's write lease (or its handle lock).</summary>
  private void _FlushPathLocked(string normalized) {
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

        // the checks above are TOCTOU on their own — hold the path exclusively for the whole
        // copy-and-free sequence so a foreground write cannot land between the re-validation
        // and the landing delete (SAFE-NOLOSS). A file a foreground op owns right now is
        // skipped rather than waited on: the drainer must never stall the pump.
        using var lease = this._handles.TryAcquireWrite(path, TimeSpan.Zero);
        if (lease == null)
          continue;

        if (this._writeBuffer.IsDirty(path) || this._handles.IsOpen(path))
          continue; // re-checked under the lease

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

    // hold the path for the whole promote/copy sequence: the guards above are a TOCTOU check,
    // so without the lease a file that goes active right after them would be republished from a
    // stale source over acknowledged data (SAFE-NOLOSS). Never wait — a busy file heals later.
    using var lease = this._handles.TryAcquireWrite(normalized, TimeSpan.Zero);
    if (lease == null)
      return;

    if (this._staging.ContainsKey(normalized) || this._writeBuffer.IsDirty(normalized) || this._handles.IsOpen(normalized))
      return; // re-checked under the lease

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
    var seen = new HashSet<string>(PoolPaths.PathComparer);
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

  /// <summary>
  /// The application has closed its last handle on this file, even though the host filesystem
  /// driver may keep the handle alive for a while yet (SAFE-DUP).
  ///
  /// WinFsp sends CLEANUP at this moment and CLOSE only when the kernel releases the file object,
  /// which it defers — often until unmount. Until this is recorded, the file still counts as open,
  /// and the drainer and the healer both skip open files, so a file written by a long-running
  /// application would never be drained to capacity nor healed back to its duplication level. That
  /// is a durability loss rather than a delay: the owed second copy is never made while the
  /// application that wrote the file keeps running.
  /// </summary>
  public void MarkApplicationClosed(NodeHandle handle) {
    this._handles.MarkApplicationClosed(handle);

    // publication hangs off "no application still has it open", which is exactly what just changed
    var path = this._handles.TryGetPath(handle);
    if (path != null && this._staging.ContainsKey(path) && !this._handles.IsOpen(path))
      this._PublishStaged(path);
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
    using var lease = this._handles.AcquireWrite(normalized);
    this._PublishStagedLocked(normalized);
  }

  /// <summary>The publication itself; the caller holds this path's write lease.</summary>
  private void _PublishStagedLocked(string normalized) {
    if (!this._staging.ContainsKey(normalized))
      return;

    this._FlushPathLocked(normalized); // owed blocks land in the temp physical first (mapping still active)
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
