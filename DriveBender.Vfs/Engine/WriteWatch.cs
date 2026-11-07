namespace DivisonM.Vfs.Engine;

/// <summary>
/// "Has this file's content changed since I started copying it?" — for the jobs that copy a file
/// without holding it (the healer and the landing-zone drainer) and must refuse to publish a copy of
/// an older version.
///
/// They used to answer it from the source's length and modification time. A rewrite that keeps the
/// length and lands within one tick of the clock changes neither, so the healer published the
/// previous version as a file's second copy, and reads split across the copies returned a stale
/// block: an acknowledged write, rolled back. A counter cannot be fooled that way.
///
/// Only watched paths are counted, so the cost when no job is copying anything is one volatile read
/// per write. A write reports itself AFTER its bytes have landed, so a copy that began while the
/// write was in flight sees the change at its commit.
/// </summary>
public sealed class WriteWatch {

  private readonly Dictionary<string, (long Changes, int Watchers)> _watched = new(PoolPaths.PathComparer);
  private readonly Lock _lock = new();
  private int _count;

  /// <summary>Starts watching; returns the token to compare against at the end.</summary>
  public long Begin(string path) {
    lock (this._lock) {
      var (changes, watchers) = this._watched.TryGetValue(path, out var entry) ? entry : (0, 0);
      this._watched[path] = (changes, watchers + 1);
      Volatile.Write(ref this._count, this._watched.Count);
      return changes;
    }
  }

  /// <summary>Whether the path's content changed since <see cref="Begin"/> returned <paramref name="token"/>.</summary>
  public bool Changed(string path, long token) {
    lock (this._lock)
      return !this._watched.TryGetValue(path, out var entry) || entry.Changes != token;
  }

  /// <summary>
  /// Counts a change for the watchers only, without telling the <see cref="Listener"/>: a repair that
  /// restored content the block index already vouches for must still stop a job that read the file
  /// before it, but the index itself stays true.
  /// </summary>
  public void Bump(string path) {
    if (Volatile.Read(ref this._count) == 0)
      return;

    lock (this._lock)
      if (this._watched.TryGetValue(path, out var entry))
        this._watched[path] = (entry.Changes + 1, entry.Watchers);
  }

  /// <summary>How many jobs are watching this path right now.</summary>
  public int Watchers(string path) {
    if (Volatile.Read(ref this._count) == 0)
      return 0;

    lock (this._lock)
      return this._watched.TryGetValue(path, out var entry) ? entry.Watchers : 0;
  }

  /// <summary>Whether a job is copying this path right now (it holds a watch on it).</summary>
  public bool IsWatched(string path) {
    if (Volatile.Read(ref this._count) == 0)
      return false;

    lock (this._lock)
      return this._watched.ContainsKey(path);
  }

  /// <summary>Stops watching (once per <see cref="Begin"/>).</summary>
  public void End(string path) {
    lock (this._lock) {
      if (!this._watched.TryGetValue(path, out var entry))
        return;

      if (entry.Watchers <= 1)
        this._watched.Remove(path);
      else
        this._watched[path] = (entry.Changes, entry.Watchers - 1);

      Volatile.Write(ref this._count, this._watched.Count);
    }
  }

  /// <summary>
  /// Told of every change, watched or not — what must stop believing anything recorded about the
  /// path's old content (the block indexes). Runs on the writer's thread, under its lease.
  /// </summary>
  public Action<string>? Listener { get; set; }

  /// <summary>Reports that <paramref name="path"/>'s content changed — call once the bytes have landed.</summary>
  public void Changed(string path) {
    this.Listener?.Invoke(path);
    if (Volatile.Read(ref this._count) == 0)
      return; // nothing is being copied: the common case costs this read and nothing else

    lock (this._lock)
      if (this._watched.TryGetValue(path, out var entry))
        this._watched[path] = (entry.Changes + 1, entry.Watchers);
  }

}
