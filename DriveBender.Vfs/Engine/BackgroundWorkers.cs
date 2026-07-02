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
      }
    }

    return worked;
  }

  /// <summary>Pumps until no job has work left (used by clean unmount, FR-CLEAN-UNMOUNT).</summary>
  public void Quiesce(int safetyLimit = 100_000) {
    while (safetyLimit-- > 0 && this.Pump() > 0) {
      // drain until idle
    }
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

/// <summary>Polls member reachability so drive loss/return is reacted to per policy (§10 SAFE-DEGRADE).</summary>
public sealed class MemberWatchJob(PoolFileSystem fs) : IBackgroundJob {

  public string Name => "member-watch";

  public bool RunOnce() => fs.PollMembers();

}
