namespace DivisonM.Vfs.Engine;

/// <summary>A remembered namespace node: its kind and last-known size/mtime.</summary>
public sealed record NamespaceNode(NodeKind Kind, long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// An always-maintained in-memory map of every path the pool has recently surfaced, so that
/// under <see cref="MemberLossPolicy.RetainMetadata"/> the mounted view can still present
/// complete metadata after a member drops out (§10 SAFE-DEGRADE). Entries are recorded as the
/// engine lists/stats/mutates paths and removed on delete; a lost member never erases them.
///
/// Bounded by an LRU cap so it cannot grow without limit on a multi-million-file pool: the
/// least-recently-recorded paths are evicted past the cap (their metadata simply stops being
/// remembered after a member loss — the data itself is untouched). Listing/statting a path
/// re-records it, so the retained set tracks the working set.
/// </summary>
public sealed class ShadowNamespace {

  private const int _DEFAULT_MAX_ENTRIES = 1_000_000;

  private readonly int _maxEntries;
  private readonly Dictionary<string, (NamespaceNode node, LinkedListNode<string> lru)> _nodes = new(StringComparer.OrdinalIgnoreCase);
  private readonly LinkedList<string> _order = new(); // MRU at the front, LRU at the back
  private readonly Lock _lock = new();

  public ShadowNamespace(int maxEntries = _DEFAULT_MAX_ENTRIES) => this._maxEntries = Math.Max(1, maxEntries);

  public int Count {
    get {
      lock (this._lock)
        return this._nodes.Count;
    }
  }

  public void Record(string normalizedPath, NamespaceNode node) {
    if (normalizedPath.Length == 0)
      return;

    lock (this._lock) {
      if (this._nodes.TryGetValue(normalizedPath, out var existing)) {
        // refresh the value and mark most-recently-used
        this._order.Remove(existing.lru);
        this._order.AddFirst(existing.lru);
        this._nodes[normalizedPath] = (node, existing.lru);
        return;
      }

      var lru = this._order.AddFirst(normalizedPath);
      this._nodes[normalizedPath] = (node, lru);

      // evict the least-recently-recorded paths past the cap (their metadata is forgotten; the
      // on-disk data is never touched)
      while (this._nodes.Count > this._maxEntries && this._order.Last is { } victim) {
        this._order.RemoveLast();
        this._nodes.Remove(victim.Value);
      }
    }
  }

  public void Remove(string normalizedPath) {
    lock (this._lock) {
      this._RemoveLocked(normalizedPath);

      // a removed directory takes its subtree with it
      var prefix = normalizedPath + "/";
      foreach (var key in this._nodes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        this._RemoveLocked(key);
    }
  }

  private void _RemoveLocked(string key) {
    if (this._nodes.Remove(key, out var entry))
      this._order.Remove(entry.lru);
  }

  public void Rename(string fromNormalized, string toNormalized) {
    lock (this._lock) {
      if (this._nodes.Remove(fromNormalized, out var moved)) {
        this._order.Remove(moved.lru);
        this._nodes[toNormalized] = (moved.node, this._order.AddFirst(toNormalized));
      }

      var fromPrefix = fromNormalized + "/";
      foreach (var key in this._nodes.Keys.Where(k => k.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray()) {
        this._nodes.Remove(key, out var child);
        this._order.Remove(child.lru);
        var newKey = toNormalized + "/" + key[fromPrefix.Length..];
        this._nodes[newKey] = (child.node, this._order.AddFirst(newKey));
      }
    }
  }

  public NamespaceNode? Get(string normalizedPath) {
    lock (this._lock)
      return this._nodes.TryGetValue(normalizedPath, out var entry) ? entry.node : null;
  }

  /// <summary>The immediate children of a folder as remembered — used to complete a listing when live members are missing entries.</summary>
  public IReadOnlyList<DirEntry> Children(string normalizedFolder) {
    var prefix = normalizedFolder.Length == 0 ? "" : normalizedFolder + "/";
    lock (this._lock) {
      var result = new List<DirEntry>();
      foreach (var (path, entry) in this._nodes) {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
          continue;

        var rest = path[prefix.Length..];
        if (rest.Length == 0 || rest.Contains('/'))
          continue; // not an immediate child

        result.Add(new(rest, entry.node.Kind, entry.node.Length, entry.node.LastWriteTimeUtc, entry.node.LastWriteTimeUtc));
      }

      return result;
    }
  }

  public IReadOnlyList<string> AllPaths() {
    lock (this._lock)
      return [.. this._nodes.Keys];
  }

  public void Clear() {
    lock (this._lock) {
      this._nodes.Clear();
      this._order.Clear();
    }
  }

}
