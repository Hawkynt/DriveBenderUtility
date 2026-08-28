using DivisonM.Vfs.Caching;

namespace DivisonM.Vfs.Engine;

/// <summary>One physical copy of a logical file: which member holds it, and whether as shadow.</summary>
public sealed record PhysicalCopy(IVolumeIO Volume, bool Shadow);

/// <summary>
/// Path → placement (CMP-PLACE): resolves a pool-relative path to its physical copies
/// (primaries first), and decides where new data lands — highest eligible tier first,
/// member by configured strategy, shadows never in the primary's failure domain
/// (SAFE-PHYS).
/// </summary>
public sealed class PlacementResolver(Guid poolId, IReadOnlyList<IVolumeIO> members, MetadataCache metadata, PoolConfig config, IReadOnlyDictionary<Guid, MemberRole>? memberRoles = null, IReadOnlyDictionary<Guid, long>? memberReserves = null,
  Func<IVolumeIO, double>? loadOf = null) {

  private int _roundRobinCounter;
  private IReadOnlyDictionary<Guid, MemberRole>? _roles = memberRoles;

  /// <summary>Swaps the tuning config live (CFG.reload); placement decisions use the new values immediately.</summary>
  public void UpdateConfig(PoolConfig newConfig) => config = newConfig;

  /// <summary>Swaps the member-role map live (reconfigure storage without remount): new writes place by the new tiers immediately.</summary>
  public void UpdateRoles(IReadOnlyDictionary<Guid, MemberRole> roles) => this._roles = roles;

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  /// <summary>All physical copies of a path, primaries before shadows (FR-RESOLVE); cached in the metadata cache.</summary>
  public IReadOnlyList<PhysicalCopy> ResolveCopies(string path) {
    var normalized = PoolPaths.Normalize(path);
    var key = new MetadataKey(poolId, normalized, MetadataKind.Placement);
    if (metadata.TryGet<IReadOnlyList<PhysicalCopy>>(key, out var cached)) {
      // A cached list can outlive a member going offline, so only reachable copies are ever handed
      // back (§10 SAFE-DEGRADE) — but the filtered list is now MATERIALISED only when a member has
      // actually dropped. Rebuilding an identical list ran on every read, write and stat.
      for (var index = 0; index < cached.Count; ++index)
        if (!cached[index].Volume.IsOnline)
          return [.. cached.Where(c => c.Volume.IsOnline)];

      return cached;
    }

    var copies = new List<PhysicalCopy>();
    foreach (var member in this._Online)
      if (member.FileExists(normalized, false))
        copies.Add(new(member, false));

    foreach (var member in this._Online)
      if (member.FileExists(normalized, true))
        copies.Add(new(member, true));

    IReadOnlyList<PhysicalCopy> result = copies;
    metadata.Put(key, result);
    return result;
  }

  public void Invalidate(string path) => metadata.InvalidatePath(poolId, path);

  /// <summary>Drops every cached placement (used on member online/offline transitions).</summary>
  public void InvalidateAll() => metadata.InvalidatePool(poolId);

  /// <summary>Duplication level D (total copies) effective for a path's folder (§6.3).</summary>
  public int DuplicationLevelFor(string folderPath) {
    var effective = ConfigResolver.ResolveForFolder(config, folderPath);
    return Math.Max(1, effective.Duplication ?? 1);
  }

  /// <summary>
  /// Free space a member will actually lend the pool: what the device reports, less the reserve the
  /// manifest set aside on it.
  ///
  /// A reserve is a promise to leave room — for the host filesystem, for another tenant, for the
  /// headroom a nearly-full disk needs to stay healthy. Placement ignored it entirely and looked
  /// only at raw free space, so the pool would keep filling a member straight through its reserve
  /// and only stop when the device itself refused, which is the one moment there is no room left
  /// to fail gracefully in.
  /// </summary>
  private long _UsableFree(IVolumeIO member) {
    var reserve = memberReserves != null && memberReserves.TryGetValue(member.MemberId, out var bytes) ? bytes : 0;
    return Math.Max(0, member.BytesFree - reserve);
  }

  private bool _IsEligible(IVolumeIO member, long size, MemberRole? roleFilter) {
    if (!member.IsOnline || this._UsableFree(member) < size)
      return false;

    var role = this._RoleOf(member);
    if (role == MemberRole.ReadOnly)
      return false;

    return roleFilter == null || role == roleFilter;
  }

  private MemberRole _RoleOf(IVolumeIO member)
    => this._roles != null && this._roles.TryGetValue(member.MemberId, out var role) ? role : MemberRole.Capacity;

  /// <summary>
  /// Picks the member for a new primary (FR-PLACE): fast tier first when one exists and
  /// has room above its low watermark, else capacity; within a tier by the configured
  /// strategy.
  /// </summary>
  public IVolumeIO? ChoosePrimaryTarget(long size) {
    var fast = this._CandidatesRespectingWatermark(size, MemberRole.Landing, this._LowWatermarkFraction("fast"));
    var choice = this._PickByStrategy(fast);
    if (choice != null)
      return choice;

    var capacity = this._CandidatesRespectingWatermark(size, MemberRole.Capacity, this._LowWatermarkFraction("capacity"));
    choice = this._PickByStrategy(capacity);
    if (choice != null)
      return choice;

    // last resort: any writable member with room, ignoring watermarks (better full than failing)
    return this._PickByStrategy([.. this._Online.Where(m => this._IsEligible(m, size, null))]);
  }

  /// <summary>
  /// Picks the member for the next shadow copy: most free space, preferring an independent
  /// failure domain (SAFE-PHYS — real redundancy against disk loss). If none has room it returns
  /// null (the caller records owed duplication) UNLESS the pool opted out of
  /// <c>placement.shadowNeverSamePhysical</c>, in which case it falls back to another member on an
  /// already-used disk — that copy guards against bit-rot/corruption but not disk failure.
  /// </summary>
  public IVolumeIO? ChooseShadowTarget(long size, IEnumerable<IVolumeIO> existingCopyHolders) {
    var holders = existingCopyHolders.ToArray();
    var occupiedDomains = new HashSet<string>(holders.Select(m => m.PhysicalVolumeId), StringComparer.OrdinalIgnoreCase);

    var independent = this._Online
      .Where(m => this._IsEligible(m, size, null) && !occupiedDomains.Contains(m.PhysicalVolumeId))
      .OrderByDescending(this._UsableFree)
      .FirstOrDefault();
    if (independent != null)
      return independent;

    // no independent domain left — only co-locate on an occupied disk if the pool allows it,
    // and never on a member that already holds a copy of this file (that would be the same data)
    if (config.Placement?.ShadowNeverSamePhysical == false) {
      var holderIds = new HashSet<Guid>(holders.Select(m => m.MemberId));
      return this._Online
        .Where(m => this._IsEligible(m, size, null) && !holderIds.Contains(m.MemberId))
        .OrderByDescending(this._UsableFree)
        .FirstOrDefault();
    }

    return null;
  }

  /// <summary>
  /// Picks the capacity member a landing-zone file drains to (FR-LZ-DRAIN): capacity
  /// role, below its low watermark, and never a failure domain already holding a copy.
  /// </summary>
  public IVolumeIO? ChooseDrainTarget(long size, IEnumerable<IVolumeIO> existingCopyHolders) {
    var occupiedDomains = new HashSet<string>(existingCopyHolders.Select(m => m.PhysicalVolumeId), StringComparer.OrdinalIgnoreCase);
    var candidates = this._CandidatesRespectingWatermark(size, MemberRole.Capacity, this._LowWatermarkFraction("capacity"))
      .Where(m => !occupiedDomains.Contains(m.PhysicalVolumeId))
      .ToArray();

    if (candidates.Length == 0)
      candidates = [.. this._Online.Where(m => this._IsEligible(m, size, MemberRole.Capacity) && !occupiedDomains.Contains(m.PhysicalVolumeId))];

    return this._PickByStrategy(candidates);
  }

  private double _LowWatermarkFraction(string tier) {
    var text = config.Tiers?.GetValueOrDefault(tier)?.LowWatermark;
    return text == null ? 1.0 : (SizeSpec.Parse(text).Percent ?? 100) / 100.0;
  }

  private IVolumeIO[] _CandidatesRespectingWatermark(long size, MemberRole role, double lowWatermarkFraction)
    => [.. this._Online.Where(m => this._IsEligible(m, size, role) && this._UsedFractionAfter(m, size) <= lowWatermarkFraction)];

  private double _UsedFractionAfter(IVolumeIO member, long size)
    => member.BytesTotal == 0 ? 1.0 : (double)(member.BytesTotal - member.BytesFree + size) / member.BytesTotal;

  private IVolumeIO? _PickByStrategy(IVolumeIO[] candidates) {
    if (candidates.Length == 0)
      return null;

    return (config.Placement?.Strategy ?? PlacementStrategy.MostFreeSpace) switch {
      // spreads consecutive new files across members — parallel spindles, lower per-file latency, higher aggregate throughput
      PlacementStrategy.RoundRobin => candidates[Interlocked.Increment(ref this._roundRobinCounter) % candidates.Length],
      PlacementStrategy.LeastUsed => candidates.OrderBy(m => m.BytesTotal - m.BytesFree).First(),
      // live measurements (EWMA over real I/O) — unmeasured members rank last, free space breaks ties
      PlacementStrategy.LowestLatency => candidates
        .OrderBy(m => m is MeasuredVolumeIO { Samples: > 0 } measured ? measured.AverageLatencyMs : double.MaxValue)
        .ThenByDescending(m => m.BytesFree)
        .First(),
      _ => this._BusiestLast(candidates),
    };
  }

  /// <summary>
  /// The default: the storage with the least work already in flight, with free space breaking ties.
  ///
  /// Picking purely by free space sends CONSECUTIVE new files to the same member, because writing
  /// one barely moves its free space — so a burst of small files queues on one device while the
  /// others sit idle, and the pool delivers one disk's IOPS however many it has. The engine already
  /// counts what each member has outstanding (it routes individual blocks by exactly this measure);
  /// this simply lets the same knowledge decide where a NEW file goes.
  ///
  /// Free space still decides when nothing is in flight, so an idle pool fills evenly exactly as
  /// before and capacity balancing is unchanged. The load term only breaks the tie that a burst
  /// creates — which is the moment it matters.
  ///
  /// Note for anyone benchmarking this on one physical disk: it will show nothing. Two members on
  /// one device share one queue, so spreading across them buys no parallelism. The gain is real
  /// only where the members are genuinely separate hardware.
  /// </summary>
  private IVolumeIO _BusiestLast(IVolumeIO[] candidates) {
    if (candidates.Length == 1 || loadOf == null)
      return candidates.OrderByDescending(m => m.BytesFree).First();

    return candidates
      .OrderBy(loadOf)
      .ThenByDescending(m => m.BytesFree)
      .First();
  }

}
