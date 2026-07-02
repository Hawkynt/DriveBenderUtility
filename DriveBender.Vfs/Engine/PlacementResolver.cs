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
public sealed class PlacementResolver(Guid poolId, IReadOnlyList<IVolumeIO> members, MetadataCache metadata, PoolConfig config, IReadOnlyDictionary<Guid, MemberRole>? memberRoles = null) {

  private int _roundRobinCounter;

  /// <summary>Swaps the tuning config live (CFG.reload); placement decisions use the new values immediately.</summary>
  public void UpdateConfig(PoolConfig newConfig) => config = newConfig;

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  /// <summary>All physical copies of a path, primaries before shadows (FR-RESOLVE); cached in the metadata cache.</summary>
  public IReadOnlyList<PhysicalCopy> ResolveCopies(string path) {
    var normalized = PoolPaths.Normalize(path);
    var key = new MetadataKey(poolId, normalized, MetadataKind.Placement);
    if (metadata.TryGet<IReadOnlyList<PhysicalCopy>>(key, out var cached))
      // a cached list can outlive a member going offline; only ever hand back reachable copies (§10 SAFE-DEGRADE)
      return [.. cached.Where(c => c.Volume.IsOnline)];

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

  private bool _IsEligible(IVolumeIO member, long size, MemberRole? roleFilter) {
    if (!member.IsOnline || member.BytesFree < size)
      return false;

    var role = this._RoleOf(member);
    if (role == MemberRole.ReadOnly)
      return false;

    return roleFilter == null || role == roleFilter;
  }

  private MemberRole _RoleOf(IVolumeIO member)
    => memberRoles != null && memberRoles.TryGetValue(member.MemberId, out var role) ? role : MemberRole.Capacity;

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
  /// Picks the member for the next shadow copy: most free space, never the failure
  /// domain of an existing copy (SAFE-PHYS). Null when no independent domain has room —
  /// the caller records owed duplication instead of co-locating copies.
  /// </summary>
  public IVolumeIO? ChooseShadowTarget(long size, IEnumerable<IVolumeIO> existingCopyHolders) {
    var occupiedDomains = new HashSet<string>(existingCopyHolders.Select(m => m.PhysicalVolumeId), StringComparer.OrdinalIgnoreCase);
    return this._Online
      .Where(m => this._IsEligible(m, size, null) && !occupiedDomains.Contains(m.PhysicalVolumeId))
      .OrderByDescending(m => m.BytesFree)
      .FirstOrDefault();
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
      PlacementStrategy.RoundRobin => candidates[Interlocked.Increment(ref this._roundRobinCounter) % candidates.Length],
      PlacementStrategy.LeastUsed => candidates.OrderBy(m => m.BytesTotal - m.BytesFree).First(),
      _ => candidates.OrderByDescending(m => m.BytesFree).First(),
    };
  }

}
