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
  private readonly Dictionary<string, (NamespaceNode node, LinkedListNode<string> lru)> _nodes = new(PoolPaths.PathComparer);
  private readonly LinkedList<string> _order = new(); // MRU at the front, LRU at the back

  // Parent folder -> the recorded paths directly inside it. Children, and the subtree walk behind
  // Remove and Rename, used to scan EVERY remembered path — every directory listing, and every
  // single-file delete or rename — so their cost grew with the pool rather than with the folder,
  // under the one lock the whole namespace shares. Keyed by the parent PATH, not the parent node:
  // a leaf can be recorded while its folder never was, and it must still be found.
  private readonly Dictionary<string, HashSet<string>> _children = new(PoolPaths.PathComparer);

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

      this._AddLocked(normalizedPath, node);

      // evict the least-recently-recorded paths past the cap (their metadata is forgotten; the
      // on-disk data is never touched)
      while (this._nodes.Count > this._maxEntries && this._order.Last is { } victim)
        this._RemoveLocked(victim.Value);
    }
  }

  public void Remove(string normalizedPath) {
    lock (this._lock) {
      var wasFile = this._nodes.TryGetValue(normalizedPath, out var entry) && entry.node.Kind == NodeKind.File;
      this._RemoveLocked(normalizedPath);

      // a removed directory takes its subtree with it; a file has none to walk
      if (!wasFile)
        foreach (var key in this._SubtreeLocked(normalizedPath))
          this._RemoveLocked(key);
    }
  }

  public void Rename(string fromNormalized, string toNormalized) {
    lock (this._lock) {
      var wasFile = false;
      if (this._nodes.TryGetValue(fromNormalized, out var moved)) {
        wasFile = moved.node.Kind == NodeKind.File;
        this._RemoveLocked(fromNormalized);
        this._RemoveLocked(toNormalized); // a rename over an existing name replaces it
        this._AddLocked(toNormalized, moved.node);
      }

      if (wasFile)
        return;

      var fromPrefix = fromNormalized + "/";
      foreach (var key in this._SubtreeLocked(fromNormalized)) {
        var child = this._nodes[key];
        this._RemoveLocked(key);
        this._AddLocked(toNormalized + "/" + key[fromPrefix.Length..], child.node);
      }
    }
  }

  public NamespaceNode? Get(string normalizedPath) {
    lock (this._lock)
      return this._nodes.TryGetValue(normalizedPath, out var entry) ? entry.node : null;
  }

  /// <summary>The immediate children of a folder as remembered — used to complete a listing when live members are missing entries.</summary>
  public IReadOnlyList<DirEntry> Children(string normalizedFolder) {
    lock (this._lock) {
      if (!this._children.TryGetValue(normalizedFolder, out var paths))
        return [];

      var result = new List<DirEntry>(paths.Count);
      foreach (var path in paths) {
        var node = this._nodes[path].node;
        var name = normalizedFolder.Length == 0 ? path : path[(normalizedFolder.Length + 1)..];
        result.Add(new(name, node.Kind, node.Length, node.LastWriteTimeUtc, node.LastWriteTimeUtc));
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
      this._children.Clear();
    }
  }

  private void _AddLocked(string key, NamespaceNode node) {
    this._nodes[key] = (node, this._order.AddFirst(key));
    var parent = PoolPaths.GetParent(key);
    if (!this._children.TryGetValue(parent, out var siblings))
      this._children[parent] = siblings = new(PoolPaths.PathComparer);

    siblings.Add(key);
  }

  private void _RemoveLocked(string key) {
    if (!this._nodes.Remove(key, out var entry))
      return;

    this._order.Remove(entry.lru);
    var parent = PoolPaths.GetParent(key);
    if (this._children.TryGetValue(parent, out var siblings)) {
      siblings.Remove(key);
      if (siblings.Count == 0)
        this._children.Remove(parent);
    }
  }

  /// <summary>
  /// Every recorded path below a folder. Found by prefix over the index's FOLDER keys rather than
  /// by descending from the root, because a folder that was never recorded itself (only its leaves
  /// were) is nobody's child in the index and a descent would miss everything under it. Folders are
  /// far fewer than paths, and a file — the common case for Remove and Rename — never gets here.
  /// </summary>
  private List<string> _SubtreeLocked(string root) {
    var prefix = root + "/";
    var found = new List<string>();
    foreach (var (parent, siblings) in this._children)
      if (parent.Equals(root, PoolPaths.PathComparison) || parent.StartsWith(prefix, PoolPaths.PathComparison))
        found.AddRange(siblings);

    return found;
  }

}
