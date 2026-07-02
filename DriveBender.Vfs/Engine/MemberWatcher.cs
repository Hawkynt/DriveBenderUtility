namespace DivisonM.Vfs.Engine;

/// <summary>
/// Watches member reachability while a pool is mounted and fires transition events so the
/// engine can react per the drive-loss policy (§10 SAFE-DEGRADE) and reconcile owed work
/// when a member returns (SAFE-OFFLINE). Poll-based so it works for every backend,
/// including remote ones; the host pumps it (a timer in production, deterministically in
/// tests).
/// </summary>
public sealed class MemberWatcher(IReadOnlyList<IVolumeIO> members) {

  private readonly Dictionary<Guid, bool> _online = members.ToDictionary(m => m.MemberId, m => true);
  private readonly Lock _lock = new();

  public event Action<IVolumeIO>? MemberLost;
  public event Action<IVolumeIO>? MemberReturned;

  /// <summary>Polls every member once and raises transition events; returns true when any state changed.</summary>
  public bool Poll() {
    var transitions = new List<(IVolumeIO member, bool nowOnline)>();
    lock (this._lock) {
      foreach (var member in members) {
        var nowOnline = member.IsOnline;
        if (this._online.TryGetValue(member.MemberId, out var was) && was == nowOnline)
          continue;

        this._online[member.MemberId] = nowOnline;
        transitions.Add((member, nowOnline));
      }
    }

    foreach (var (member, nowOnline) in transitions) {
      if (nowOnline) {
        DriveBender.Logger($"Member '{member.DisplayName}' is back online");
        this.MemberReturned?.Invoke(member);
      } else {
        DriveBender.Logger($"[Warning]Member '{member.DisplayName}' dropped out");
        this.MemberLost?.Invoke(member);
      }
    }

    return transitions.Count > 0;
  }

  public bool IsConsideredOnline(Guid memberId) {
    lock (this._lock)
      return this._online.GetValueOrDefault(memberId, false);
  }

}
