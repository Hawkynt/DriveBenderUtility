namespace DivisonM.Vfs.Engine;

public sealed record MediaLifecycleReport(int FilesMoved, int CopiesCreated, int CopiesRemoved) {
  public bool AnythingDone => this.FilesMoved + this.CopiesCreated + this.CopiesRemoved > 0;
}

/// <summary>
/// Administrative media operations over a pool's members (§1.1 whole-file model):
/// scatter-and-remove a member, replace a member, and restore the pool to its
/// duplication level. Each operation moves whole files via <see cref="WholeFilePublisher"/>
/// under journalled intents, honours failure domains (SAFE-PHYS), and only ever removes a
/// copy once another exists (SAFE-DUP) — safe to interrupt and resume.
/// </summary>
public sealed class MediaLifecycle(IReadOnlyList<IVolumeIO> members, Journal journal, int duplicationLevel) {

  private sealed record Copy(IVolumeIO Member, string Path, bool Shadow);

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

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

  private static byte[] _Read(Copy copy) {
    using var stream = copy.Member.OpenRead(copy.Path, copy.Shadow);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private IVolumeIO? _ChooseTarget(IEnumerable<Copy> existing, long size, IVolumeIO? exclude) {
    var occupiedDomains = new HashSet<string>(existing.Select(c => c.Member.PhysicalVolumeId), StringComparer.OrdinalIgnoreCase);
    return this._Online
      .Where(m => m != exclude && m.BytesFree >= size && !occupiedDomains.Contains(m.PhysicalVolumeId))
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
        // ensure the content survives on another domain before deleting this copy
        var survivesElsewhere = elsewhere.Any(c => c.Member.PhysicalVolumeId != leaving.PhysicalVolumeId);
        if (!survivesElsewhere) {
          var content = _Read(copy);
          var target = this._ChooseTarget(elsewhere, content.LongLength, leaving)
                       ?? throw new PoolFsException(PoolFsError.NoSpace, $"Nowhere to move '{path}' off '{leaving.DisplayName}'");

          var sequence = journal.LogIntent(JournalOp.Rebalance, path, memberId: target.MemberId);
          var parent = PoolPaths.GetParent(path);
          if (parent.Length > 0)
            target.EnsureFolder(parent, copy.Shadow);

          WholeFilePublisher.Publish(target, path, copy.Shadow, content);
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
      var content = _Read(new(old, path, shadow));
      var sequence = journal.LogIntent(JournalOp.Rebalance, path, memberId: replacement.MemberId);
      var parent = PoolPaths.GetParent(path);
      if (parent.Length > 0)
        replacement.EnsureFolder(parent, shadow);

      WholeFilePublisher.Publish(replacement, path, shadow, content);
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
      var distinctDomains = copies.Select(c => c.Member.PhysicalVolumeId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
      var hasPrimary = copies.Any(c => !c.Shadow);
      var source = copies[0];
      byte[]? content = null;

      // promote a shadow to primary when no primary survives (FixMissingPrimaries)
      if (!hasPrimary) {
        content ??= _Read(source);
        var sequence = journal.LogIntent(JournalOp.ShadowCreate, path, memberId: source.Member.MemberId);
        WholeFilePublisher.Publish(source.Member, path, false, content);
        source.Member.Delete(path, true);
        journal.Complete(sequence, JournalOp.ShadowCreate);
        copies[0] = source with { Shadow = false };
        ++created;
      }

      // create missing shadow copies up to the duplication level (FixMissingShadowCopies)
      while (distinctDomains < duplicationLevel) {
        content ??= _Read(copies[0]);
        var target = this._ChooseTarget(copies, content.LongLength, null);
        if (target == null)
          break; // not placeable without co-locating (SAFE-PHYS) — leave for later

        var sequence = journal.LogIntent(JournalOp.ShadowCreate, path, memberId: target.MemberId);
        var parent = PoolPaths.GetParent(path);
        if (parent.Length > 0)
          target.EnsureFolder(parent, true);

        WholeFilePublisher.Publish(target, path, true, content);
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
