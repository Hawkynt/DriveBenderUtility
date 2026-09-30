namespace DivisonM.Vfs.Engine;

/// <summary>One watched pool path: whether anything changed it since the watch began.</summary>
public interface IPathWatch : IDisposable {
  string Path { get; }
  bool Changed { get; }
}

/// <summary>
/// How a background job works with files WITHOUT holding them (docs/SpaceSavings.md). It watches a
/// file, reads and prepares at leisure while applications keep using the file, and asks for a short
/// commit window only for the final step. The window is refused, never waited for, when the file
/// changed meanwhile or is in use right now; the job then leaves the file for its next pass.
/// </summary>
public interface IChangeFeed {
  /// <summary>Starts watching a pool path; dispose the watch when done.</summary>
  IPathWatch Watch(string path);

  /// <summary>
  /// A short window in which none of the watched paths can change, or null when any of them has
  /// changed or is busy. Held only for the commit itself: a rename, a hole punch.
  /// </summary>
  IDisposable? TryCommit(IReadOnlyList<IPathWatch> watches);
}

/// <summary>
/// Changes seen on the members' filesystems directly, with no pool engine involved: what
/// <c>pool health --fix</c> relies on, and what a mounted pool adds for programs that write to a
/// member behind its back. Filesystem notifications can arrive late, so a caller also compares the
/// file's size and time at its commit.
/// </summary>
public sealed class MemberChangeFeed : IChangeFeed, IDisposable {

  private readonly List<IDisposable> _subscriptions = [];
  private readonly List<_Watch> _watches = [];
  private readonly Lock _lock = new();

  public MemberChangeFeed(IEnumerable<IVolumeIO> members) {
    foreach (var member in members)
      try {
        if (member.WatchChanges(this._OnChanged) is { } subscription)
          this._subscriptions.Add(subscription);
      } catch (PoolFsException) {
        // an unreachable member: nothing on it is committed to anyway
      }
  }

  public IPathWatch Watch(string path) {
    var watch = new _Watch(this, PoolPaths.Normalize(path));
    lock (this._lock)
      this._watches.Add(watch);

    return watch;
  }

  public IDisposable? TryCommit(IReadOnlyList<IPathWatch> watches) => watches.Any(w => w.Changed) ? null : _Nothing.Instance;

  private void _OnChanged(string? path) {
    lock (this._lock)
      foreach (var watch in this._watches)
        if (path == null || _Covers(path, watch.Path))
          watch.Mark();
  }

  /// <summary>A change to the path itself, or to a folder above it (a folder renamed or deleted).</summary>
  private static bool _Covers(string changed, string watched)
    => string.Equals(changed, watched, PoolPaths.PathComparison)
       || changed.Length == 0
       || watched.StartsWith(changed + "/", PoolPaths.PathComparison);

  public void Dispose() {
    foreach (var subscription in this._subscriptions)
      subscription.Dispose();

    this._subscriptions.Clear();
  }

  private sealed class _Watch(MemberChangeFeed owner, string path) : IPathWatch {
    private volatile bool _changed;
    public string Path => path;
    public bool Changed => this._changed;
    public void Mark() => this._changed = true;

    public void Dispose() {
      lock (owner._lock)
        owner._watches.Remove(this);
    }
  }

  private sealed class _Nothing : IDisposable {
    public static readonly _Nothing Instance = new();
    public void Dispose() { }
  }

}
