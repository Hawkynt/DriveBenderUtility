namespace DivisonM.Vfs.Engine;

/// <summary>A cooperative, resumable background job (CMP-BG). RunOnce does one bounded unit of work.</summary>
public interface IBackgroundJob {
  string Name { get; }

  /// <summary>Performs one unit of work; false = nothing left to do right now.</summary>
  bool RunOnce();
}

/// <summary>
/// Cooperative scheduler for background work (FR-BG-THROTTLE): every pump is bounded so
/// background I/O can never starve foreground traffic; hosts pump from a timer, tests
/// pump deterministically.
/// </summary>
public sealed class BackgroundScheduler(IReadOnlyList<IBackgroundJob> jobs) {

  /// <summary>
  /// How long a clean unmount waits for background housekeeping before shutting down anyway.
  ///
  /// Generous next to a healthy pool, which quiesces in well under a second, and short next to any
  /// shutdown watchdog. It only bites when something is throttled or a disk is slow, which is
  /// exactly when waiting indefinitely is the wrong answer. Lives here rather than in each host so
  /// both adapters shut down on the same terms.
  /// </summary>
  public static readonly TimeSpan UnmountBudget = TimeSpan.FromSeconds(10);

  /// <summary>Runs at most <paramref name="maxUnits"/> units of work round-robin; returns the units actually worked.</summary>
  public int Pump(int maxUnits = 16) {
    var worked = 0;
    var idle = 0;
    var index = 0;
    while (worked < maxUnits && idle < jobs.Count) {
      var job = jobs[index % jobs.Count];
      ++index;
      try {
        if (job.RunOnce()) {
          ++worked;
          idle = 0;
        } else
          ++idle;
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Background job '{job.Name}' failed a unit: {e.Message}");
        ++idle;
      } catch (OperationCanceledException) {
        // shutdown asked the copy to stop at its next chunk; the journal describes what was in
        // flight and the next mount reconciles it (see PoolFileSystem.AbortBackgroundWork)
        return worked;
      }
    }

    return worked;
  }

  /// <summary>
  /// Pumps until no job has work left, or until <paramref name="budget"/> runs out (clean unmount,
  /// FR-CLEAN-UNMOUNT).
  ///
  /// The budget exists because this waits for HOUSEKEEPING, and housekeeping runs at whatever rate
  /// the operator allowed it. Unbounded, a member held to 64 KiB/s with twenty megabytes still to
  /// drain makes the pool take five and a half minutes to unmount — the request times out, the
  /// process is killed, and the next mount replays the journal. Throttling a disk should not cost
  /// the ability to shut down cleanly, and the two settings have no business being coupled.
  ///
  /// Nothing acknowledged is at stake. Durability is <see cref="PoolFileSystem.Unmount"/>'s job:
  /// it publishes staged files and flushes every dirty path, and runs after this either way. What
  /// gets abandoned here is a drain that has not happened yet or a copy not yet caught up — both
  /// journalled, both resumed on the next mount, and both abandoned anyway by the kill this
  /// prevents.
  ///
  /// The deadline alone is not enough, which is why <paramref name="abort"/> exists: one unit of
  /// work is a WHOLE FILE, so a check between units cannot bound a copy that is itself minutes long.
  /// When the budget runs out the abort is signalled, the copy in flight stops at its next chunk,
  /// and the loop unwinds — see <see cref="PoolFileSystem.AbortBackgroundWork"/> for why abandoning
  /// it is safe.
  ///
  /// Returns false when work was still pending at the deadline.
  /// </summary>
  public bool Quiesce(TimeSpan? budget = null, Action? abort = null, int safetyLimit = 100_000) {
    var deadline = budget is { } limit ? DateTime.UtcNow + limit : DateTime.MaxValue;
    using var giveUp = budget is { } window && abort != null
      ? new Timer(_ => abort(), null, window, Timeout.InfiniteTimeSpan)
      : null;

    while (safetyLimit-- > 0) {
      if (this.Pump() == 0)
        return true; // nothing left to do

      if (DateTime.UtcNow >= deadline)
        return false;
    }

    return false;
  }

}


/// <summary>
/// The periodic bit-rot sweep (FR-SCRUB), on the cadence `integrity.scrubberSchedule` and
/// `integrity.deepScrubSchedule` already describe.
///
/// Reads are deliberately NOT verified — a pool that hashed every block it served would trade its
/// throughput away for a check that almost always passes. The bargain is that damage is found by
/// sweeping instead: writes hash what they can and mark what they cannot, and this comes round on a
/// schedule to re-baseline what is stale and re-read what is not. Without it the schedule in the
/// configuration is decoration, which is what it was — nothing ran it.
///
/// The QUICK pass is cheap: it only hashes files whose recorded metadata deviates, or whose entry a
/// write marked stale. The DEEP pass re-reads everything and is what actually finds rot, which is
/// why its default cadence is long and why it is off unless configured.
/// </summary>
public sealed class ScrubJob(PoolFileSystem fs, TimeSpan quickEvery, TimeSpan deepEvery, Func<DateTime> clock) : IBackgroundJob {

  private DateTime _lastQuick = clock();
  private DateTime _lastDeep = clock();

  public string Name => "scrub";

  public bool RunOnce() {
    var now = clock();

    // deep first when both are due: it subsumes the quick pass, so running quick as well would be
    // reading every byte twice
    if (deepEvery > TimeSpan.Zero && now - this._lastDeep >= deepEvery) {
      this._lastDeep = this._lastQuick = now;
      var issues = fs.RunScrub();
      DriveBender.Logger($" - Scheduled deep scrub finished: {issues.Count} issue(s)");
      return true;
    }

    if (quickEvery > TimeSpan.Zero && now - this._lastQuick >= quickEvery) {
      this._lastQuick = now;
      fs.RunQuickScrub();
      return true;
    }

    return false;
  }

}

/// <summary>
/// Completes writes owed to lagging copies (the write-back/deferred tail) — the engine's
/// duplicator: once it settles, every file is back at its duplication level (SAFE-DUP).
/// </summary>
public sealed class OwedSyncJob(PoolFileSystem fs, TimeSpan deferWindow, TimeSpan maxDefer) : IBackgroundJob {

  public string Name => "owed-sync";

  public bool RunOnce() {
    var expired = fs.WriteBuffer.ExpiredPaths(deferWindow, maxDefer);
    if (expired.Count == 0)
      return false;

    fs.FlushPath(expired[0]);
    return true;
  }

}

/// <summary>
/// The drainer (FR-LZ-DRAIN): moves whole files from fast-tier (landing) members down to
/// capacity members via temp + atomic rename under a journalled Drain intent, then
/// re-establishes the duplication level and frees the fast tier.
/// </summary>
public sealed class DrainJob(PoolFileSystem fs) : IBackgroundJob {

  public string Name => "drainer";

  public bool RunOnce() => fs.DrainOneLandingFile();

}

/// <summary>Applies the trash retention/size policy, purging oldest first (§6.14).</summary>
public sealed class TrashMaintenanceJob(PoolFileSystem fs) : IBackgroundJob {

  public string Name => "trash-maintenance";

  public bool RunOnce() => fs.PurgeTrash() > 0;

}

/// <summary>
/// Hands back cached OS resources the members are no longer using (CMP-BG). Pooled file handles
/// are otherwise only reclaimed when the next request arrives, so a pool nobody is using keeps
/// its members' files open — and an open handle is what makes a volume refuse to be ejected.
/// </summary>
public sealed class TrimIdleResourcesJob(IReadOnlyList<IVolumeIO> members) : IBackgroundJob {

  public string Name => "trim-idle-resources";

  public bool RunOnce() {
    foreach (var member in members)
      try {
        member.ReleaseIdleResources();
      } catch (PoolFsException) {
        // a member that cannot be asked is a member that is already gone
      }

    return false; // never counts as work: this must not keep the pump awake
  }

}

/// <summary>Polls member reachability so drive loss/return is reacted to per policy (§10 SAFE-DEGRADE).</summary>
public sealed class MemberWatchJob(PoolFileSystem fs) : IBackgroundJob {

  public string Name => "member-watch";

  public bool RunOnce() => fs.PollMembers();

}

/// <summary>
/// Restores the pool to full health after a mount or a member return (FR-HEAL): missing
/// primaries promoted, missing shadow copies recreated — incrementally, so foreground I/O
/// is never starved, and as fast as the pump allows.
/// </summary>
public sealed class HealJob(PoolFileSystem fs) : IBackgroundJob {

  public string Name => "heal";

  public bool RunOnce() => fs.HealStep();

}
