namespace DivisonM.Vfs.Engine;

/// <summary>
/// Per-member SMART health, sampled slowly in the background and read instantly.
///
/// The metrics snapshot is published every second, and asking <c>smartctl</c> anything is a process
/// launch that can take seconds and is allowed ten before it is killed. Querying on the publish path
/// would put one fork per member per second in front of a timer the mount's whole background pump
/// shares — the drain, the heal, the reload and the unmount request all ride it — so a single
/// unresponsive drive would stall pool maintenance rather than merely reporting itself slowly.
///
/// So the sampling is off that thread entirely and rate-limited: readers get whatever was last
/// learned, which for a signal that moves over weeks is exactly right. Nothing here ever throws at
/// its caller; health that cannot be read is <see cref="DiskHealth.Unknown"/>, which is a different
/// thing from unhealthy and is carried as such.
/// </summary>
public sealed class MemberSmartCache(ISmartMonitor smart, TimeSpan? interval = null, Func<DateTime>? clock = null) {

  /// <summary>SMART moves over weeks; sampling it faster costs process launches and learns nothing.</summary>
  private static readonly TimeSpan _DEFAULT_INTERVAL = TimeSpan.FromMinutes(5);

  private readonly TimeSpan _interval = interval ?? _DEFAULT_INTERVAL;
  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
  private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SmartStatus> _byMember = new();
  private DateTime _lastSweep = DateTime.MinValue;
  private int _sweeping;

  /// <summary>What is known right now about each member; empty until the first sweep has finished.</summary>
  public IReadOnlyDictionary<Guid, SmartStatus> Current => this._byMember;

  /// <summary>
  /// Returns immediately, starting a background sweep when the last one has aged out.
  ///
  /// Only ever ONE sweep at a time: a drive that takes the full smartctl timeout must not have a
  /// second query queued behind it every tick, which would end with a thread per tick all waiting on
  /// the same unresponsive device.
  /// </summary>
  public void RefreshInBackground(IReadOnlyList<IVolumeIO> members, Func<IVolumeIO, string> deviceOf) {
    if (!smart.IsSupported || members.Count == 0)
      return;

    if (this._clock() - this._lastSweep < this._interval)
      return;

    if (Interlocked.CompareExchange(ref this._sweeping, 1, 0) != 0)
      return; // a sweep is already running; it will refresh the stamp when it finishes

    new Thread(() => {
      try {
        foreach (var member in members) {
          if (!member.IsOnline) {
            // a member that is not there has nothing to say about its health, and asking would
            // block on a device that has gone; its absence is reported by the member state instead
            this._byMember.TryRemove(member.MemberId, out _);
            continue;
          }

          try {
            this._byMember[member.MemberId] = smart.Query(deviceOf(member));
          } catch (Exception e) {
            DriveBender.Logger($"[Warning]SMART query for '{member.DisplayName}' failed: {e.Message}");
            this._byMember[member.MemberId] = SmartStatus.Unavailable(member.DisplayName, e.Message);
          }
        }
      } finally {
        this._lastSweep = this._clock();
        Volatile.Write(ref this._sweeping, 0);
      }
    }) { IsBackground = true, Name = "dbmount-smart" }.Start();
  }

}
