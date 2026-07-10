namespace DivisonM.Vfs.Engine;

public sealed record MediaLifecycleReport(int FilesMoved, int CopiesCreated, int CopiesRemoved) {
  public bool AnythingDone => this.FilesMoved + this.CopiesCreated + this.CopiesRemoved > 0;
}

/// <summary>
/// Administrative media operations over a pool's members (§1.1 whole-file model):
/// scatter-and-remove a member, replace a member, and restore the pool to its
/// duplication level. Each operation moves whole files via <see cref="WholeFilePublisher"/>
/// under journalled intents, honours failure domains (SAFE-PHYS) — with
/// <paramref name="allowSamePhysical"/> as the pool's explicit opt-in to co-locate copies
/// on one disk (bit-rot protection without disk-loss protection) — and only ever removes a
/// copy once another exists (SAFE-DUP); safe to interrupt and resume.
/// </summary>
public sealed class MediaLifecycle(IReadOnlyList<IVolumeIO> members, Journal journal, int duplicationLevel, bool allowSamePhysical = false) {

  private sealed record Copy(IVolumeIO Member, string Path, bool Shadow);

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  /// <summary>How well a file is covered: independent disks by default, distinct members when same-disk copies are allowed.</summary>
  private int _Coverage(IEnumerable<Copy> copies) => allowSamePhysical
    ? copies.Select(c => c.Member.MemberId).Distinct().Count()
    : copies.Select(c => c.Member.PhysicalVolumeId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

  /// <summary>Files that are below their duplication level or missing a primary (read-only audit for health checks).</summary>
  public int CountUnderDuplicated() {
    var count = 0;
    foreach (var (_, copies) in this._EnumerateLogicalFiles())
      if (this._Coverage(copies) < duplicationLevel || !copies.Any(c => !c.Shadow))
        ++count;

    return count;
  }

  /// <summary>All logical files and where their copies live (primary + shadow), across every online member.</summary>
  private Dictionary<string, List<Copy>> _EnumerateLogicalFiles() {
    var map = new Dictionary<string, List<Copy>>(StringComparer.OrdinalIgnoreCase);
    foreach (var member in this._Online)
      foreach (var (path, shadow) in this._WalkMember(member)) {
        if (!map.TryGetValue(path, out var copies))
          map.Add(path, copies = []);

        copies.Add(new(member, path, shadow));
      }

    return map;
  }

  /// <summary>Every file physically on one member, with its shadow flag (walks both the primary tree and shadow containers).</summary>
  private IEnumerable<(string path, bool shadow)> _WalkMember(IVolumeIO member) {
    var stack = new Stack<string>();
    stack.Push("");
    while (stack.Count > 0) {
      var folder = stack.Pop();
      VolumeEntry[] primaries, shadows;
      try {
        primaries = [.. member.List(folder, false)];
      } catch (PoolFsException) {
        continue;
      }

      foreach (var entry in primaries) {
        if (PoolPaths.IsHiddenName(entry.Name))
          continue;

        var childPath = folder.Length == 0 ? entry.Name : $"{folder}/{entry.Name}";
        if (entry.IsDirectory)
          stack.Push(childPath);
        else
          yield return (childPath, false);
      }

      try {
        shadows = member.FolderExists(folder, true) ? [.. member.List(folder, true)] : [];
      } catch (PoolFsException) {
        shadows = [];
      }

      foreach (var entry in shadows)
        if (!entry.IsDirectory && !PoolPaths.IsHiddenName(entry.Name))
          yield return (folder.Length == 0 ? entry.Name : $"{folder}/{entry.Name}", true);
    }
  }

  private static long _Size(Copy copy) => copy.Member.Stat(copy.Path, copy.Shadow)?.Length ?? 0;

  private IVolumeIO? _ChooseTarget(IEnumerable<Copy> existing, long size, IVolumeIO? exclude) {
    var list = existing.ToArray();
    var occupiedDomains = new HashSet<string>(list.Select(c => c.Member.PhysicalVolumeId), StringComparer.OrdinalIgnoreCase);
    var independent = this._Online
      .Where(m => m != exclude && m.BytesFree >= size && !occupiedDomains.Contains(m.PhysicalVolumeId))
      .OrderByDescending(m => m.BytesFree)
      .FirstOrDefault();
    if (independent != null || !allowSamePhysical)
      return independent;

    // opted in: no independent disk left — use another member on an occupied disk, but never
    // one that already holds a copy of this file (that would duplicate onto itself)
    var holders = new HashSet<Guid>(list.Select(c => c.Member.MemberId));
    return this._Online
      .Where(m => m != exclude && m.BytesFree >= size && !holders.Contains(m.MemberId))
      .OrderByDescending(m => m.BytesFree)
      .FirstOrDefault();
  }

  /// <summary>
  /// Scatters a member's data over the remaining members and removes it: every file it
  /// holds is guaranteed to still exist on an independent failure domain before its copy
  /// here is deleted (SAFE-DUP), so no interruption loses data.
  /// </summary>
  public MediaLifecycleReport ScatterAndRemove(Guid memberId) {
    var leaving = members.FirstOrDefault(m => m.MemberId == memberId)
                  ?? throw new PoolFsException(PoolFsError.NotFound, $"No member {memberId} in the pool");
    if (this._Online.Count(m => m != leaving) == 0)
      throw new PoolFsException(PoolFsError.NoSpace, "No other online member to scatter onto");

    var moved = 0;
    var removed = 0;
    foreach (var (path, allCopies) in this._EnumerateLogicalFiles()) {
      var here = allCopies.Where(c => c.Member.MemberId == memberId).ToList();
      if (here.Count == 0)
        continue;

      var elsewhere = allCopies.Where(c => c.Member.MemberId != memberId).ToList();
      foreach (var copy in here) {
        // ensure the content survives on another domain (or member, when co-location is allowed) first
        var survivesElsewhere = allowSamePhysical
          ? elsewhere.Count > 0
          : elsewhere.Any(c => c.Member.PhysicalVolumeId != leaving.PhysicalVolumeId);
        if (!survivesElsewhere) {
          var size = _Size(copy);
          var target = this._ChooseTarget(elsewhere, size, leaving)
                       ?? throw new PoolFsException(PoolFsError.NoSpace, $"Nowhere to move '{path}' off '{leaving.DisplayName}'");

          var sequence = journal.LogIntent(JournalOp.Rebalance, path, memberId: target.MemberId);
          var parent = PoolPaths.GetParent(path);
          if (parent.Length > 0)
            target.EnsureFolder(parent, copy.Shadow);

          WholeFilePublisher.CopyBetween(copy.Member, path, copy.Shadow, target, path, copy.Shadow);
          journal.Complete(sequence, JournalOp.Rebalance);
          elsewhere.Add(new(target, path, copy.Shadow));
          ++moved;
        }

        copy.Member.Delete(path, copy.Shadow);
        ++removed;
      }
    }

    DriveBender.Logger($"Removed media '{leaving.DisplayName}': {moved} file(s) relocated, {removed} copy(ies) cleared");
    return new(moved, moved, removed);
  }

  /// <summary>
  /// Migrates every file from <paramref name="oldMemberId"/> onto <paramref name="replacement"/>
  /// (same primary/shadow role), then clears the old member — a like-for-like swap that keeps
  /// the failure-domain layout intact.
  /// </summary>
  public MediaLifecycleReport Replace(Guid oldMemberId, IVolumeIO replacement) {
    var old = members.FirstOrDefault(m => m.MemberId == oldMemberId)
              ?? throw new PoolFsException(PoolFsError.NotFound, $"No member {oldMemberId} in the pool");

    var moved = 0;
    foreach (var (path, shadow) in this._WalkMember(old).ToArray()) {
      var sequence = journal.LogIntent(JournalOp.Rebalance, path, memberId: replacement.MemberId);
      var parent = PoolPaths.GetParent(path);
      if (parent.Length > 0)
        replacement.EnsureFolder(parent, shadow);

      WholeFilePublisher.CopyBetween(old, path, shadow, replacement, path, shadow);
      journal.Complete(sequence, JournalOp.Rebalance);
      old.Delete(path, shadow);
      ++moved;
    }

    DriveBender.Logger($"Replaced media '{old.DisplayName}' with '{replacement.DisplayName}': {moved} file(s) migrated");
    return new(moved, moved, moved);
  }

  /// <summary>
  /// Restores the pool to its duplication level: promotes a shadow to primary where the
  /// primary is missing, and creates missing shadow copies on independent failure domains
  /// (SAFE-DUP / SAFE-PHYS). Reuses existing copies as the source — no data is fetched twice.
  /// </summary>
  public MediaLifecycleReport RestorePool() {
    var created = 0;
    foreach (var (path, copies) in this._EnumerateLogicalFiles()) {
      var distinctDomains = this._Coverage(copies);
      var hasPrimary = copies.Any(c => !c.Shadow);
      var source = copies[0];

      // promote a shadow to primary when no primary survives (FixMissingPrimaries) — streamed
      if (!hasPrimary) {
        var sequence = journal.LogIntent(JournalOp.ShadowCreate, path, memberId: source.Member.MemberId);
        WholeFilePublisher.CopyBetween(source.Member, path, true, source.Member, path, false);
        source.Member.Delete(path, true);
        journal.Complete(sequence, JournalOp.ShadowCreate);
        copies[0] = source with { Shadow = false };
        ++created;
      }

      // create missing shadow copies up to the duplication level (FixMissingShadowCopies) — streamed
      while (distinctDomains < duplicationLevel) {
        var size = _Size(copies[0]);
        var target = this._ChooseTarget(copies, size, null);
        if (target == null)
          break; // not placeable without co-locating (SAFE-PHYS) — leave for later

        var sequence = journal.LogIntent(JournalOp.ShadowCreate, path, memberId: target.MemberId);
        var parent = PoolPaths.GetParent(path);
        if (parent.Length > 0)
          target.EnsureFolder(parent, true);

        WholeFilePublisher.CopyBetween(copies[0].Member, path, copies[0].Shadow, target, path, true);
        journal.Complete(sequence, JournalOp.ShadowCreate);
        copies.Add(new(target, path, true));
        ++distinctDomains;
        ++created;
      }
    }

    DriveBender.Logger($"Restored pool: {created} copy(ies) created/promoted to reach duplication level {duplicationLevel}");
    return new(0, created, 0);
  }

}
