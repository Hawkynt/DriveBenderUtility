namespace DivisonM.Vfs.Engine;

/// <summary>One member's device health for a pool report.</summary>
public sealed record MemberHealth(string Member, SmartStatus Smart);

/// <summary>
/// A pool health snapshot (G16): device SMART/temperature, silent bit-rot and out-of-band
/// conflicts (from the checksum scrub), and under-duplication (missing primaries/shadows).
/// When run in correcting mode, the counts reflect what was fixed.
/// </summary>
public sealed record HealthReport(
  IReadOnlyList<MemberHealth> Members,
  IReadOnlyList<IntegrityIssue> IntegrityIssues,
  int UnderDuplicatedFiles,
  int CopiesRepaired,
  bool Corrected,
  bool DeepScan = false
) {
  public bool Healthy => this.UnderDuplicatedFiles == 0
    && this.IntegrityIssues.All(i => i.Kind == IntegrityIssueKind.ExternalEditAccepted)
    && this.Members.All(m => m.Smart.Health is DiskHealth.Healthy or DiskHealth.Unknown);

  public IEnumerable<MemberHealth> UnhealthyMembers => this.Members.Where(m => m.Smart.Health is DiskHealth.Warning or DiskHealth.Failing);
}

/// <summary>
/// Aggregates and corrects pool health (G16): combines SMART/temperature, checksum bit-rot
/// repair and conflict handling (via <see cref="IntegrityService"/>), and duplication
/// restoration (via <see cref="MediaLifecycle"/>) into one check-and-fix pass.
/// </summary>
public sealed class HealthService(
  IReadOnlyList<IVolumeIO> members,
  ISmartMonitor smart,
  IntegrityService integrity,
  MediaLifecycle media,
  Func<IVolumeIO, string>? deviceOf = null) {

  private readonly Func<IVolumeIO, string> _deviceOf = deviceOf ?? (m => m.PhysicalVolumeId);

  private IReadOnlyList<MemberHealth> _MemberHealth()
    => [.. members.Where(m => m.IsOnline).Select(m => new MemberHealth(m.DisplayName, smart.Query(this._deviceOf(m))))];

  /// <summary>
  /// Reports health WITHOUT changing anything: SMART/temperature, under-duplication, and an
  /// integrity detection pass. The default scan checks metadata (fast — finds missing
  /// duplicates, size mismatches, external edits); <paramref name="deep"/> re-checksums
  /// every byte to also surface silent bit-rot — opt-in because it reads the whole pool.
  /// </summary>
  public HealthReport Check(bool deep = false)
    => new(this._MemberHealth(), deep ? integrity.DetectAll() : integrity.DetectQuick(), media.CountUnderDuplicated(), 0, Corrected: false, DeepScan: deep);

  /// <summary>
  /// Full check with correction (always deep): repairs bit-rot from good copies, re-syncs
  /// stale copies, accepts/records external edits, quarantines conflicts (scrub), then
  /// restores every file to its duplication level (missing primaries promoted, missing
  /// shadows recreated). SMART/temperature are reported for alerting — hardware faults
  /// cannot be auto-fixed, only surfaced.
  /// </summary>
  public HealthReport CheckAndCorrect(Action<string>? invalidateCaches = null) {
    var issues = integrity.ScrubAll(invalidateCaches);
    var restore = media.RestorePool();
    var underDuplicatedAfter = media.CountUnderDuplicated();

    foreach (var member in this._MemberHealth().Where(m => m.Smart.Health is DiskHealth.Warning or DiskHealth.Failing))
      DriveBender.Logger($"[Alert]Device '{member.Member}' health {member.Smart.Health}: {member.Smart.Detail} (temp {member.Smart.TemperatureCelsius}°C, reallocated {member.Smart.ReallocatedSectors})");

    return new(this._MemberHealth(), issues, underDuplicatedAfter, restore.CopiesCreated, Corrected: true, DeepScan: true);
  }

}
