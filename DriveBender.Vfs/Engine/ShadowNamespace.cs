namespace DivisonM.Vfs.Engine;

/// <summary>A remembered namespace node: its kind and last-known size/mtime.</summary>
public sealed record NamespaceNode(NodeKind Kind, long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// An always-maintained in-memory map of every path the pool has surfaced, so that under
/// <see cref="MemberLossPolicy.RetainMetadata"/> the mounted view can still present
/// complete metadata after a member drops out (§10 SAFE-DEGRADE). Entries are recorded
/// as the engine lists/stats/mutates paths and removed on delete; a lost member never
/// erases them.
/// </summary>
public sealed class ShadowNamespace {

  private readonly Dictionary<string, NamespaceNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
  private readonly Lock _lock = new();

  public int Count {
    get {
      lock (this._lock)
        return this._nodes.Count;
    }
  }

  public void Record(string normalizedPath, NamespaceNode node) {
    if (normalizedPath.Length == 0)
      return;

    lock (this._lock)
      this._nodes[normalizedPath] = node;
  }

  public void Remove(string normalizedPath) {
    lock (this._lock) {
      this._nodes.Remove(normalizedPath);

      // a removed directory takes its subtree with it
      var prefix = normalizedPath + "/";
      foreach (var key in this._nodes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        this._nodes.Remove(key);
    }
  }

  public void Rename(string fromNormalized, string toNormalized) {
    lock (this._lock) {
      if (this._nodes.Remove(fromNormalized, out var node))
        this._nodes[toNormalized] = node;

      var fromPrefix = fromNormalized + "/";
      foreach (var key in this._nodes.Keys.Where(k => k.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray()) {
        var moved = this._nodes[key];
        this._nodes.Remove(key);
        this._nodes[toNormalized + "/" + key[fromPrefix.Length..]] = moved;
      }
    }
  }

  public NamespaceNode? Get(string normalizedPath) {
    lock (this._lock)
      return this._nodes.GetValueOrDefault(normalizedPath);
  }

  /// <summary>The immediate children of a folder as remembered — used to complete a listing when live members are missing entries.</summary>
  public IReadOnlyList<DirEntry> Children(string normalizedFolder) {
    var prefix = normalizedFolder.Length == 0 ? "" : normalizedFolder + "/";
    lock (this._lock) {
      var result = new List<DirEntry>();
      foreach (var (path, node) in this._nodes) {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
          continue;

        var rest = path[prefix.Length..];
        if (rest.Length == 0 || rest.Contains('/'))
          continue; // not an immediate child

        result.Add(new(rest, node.Kind, node.Length, node.LastWriteTimeUtc, node.LastWriteTimeUtc));
      }

      return result;
    }
  }

  public IReadOnlyList<string> AllPaths() {
    lock (this._lock)
      return [.. this._nodes.Keys];
  }

  public void Clear() {
    lock (this._lock)
      this._nodes.Clear();
  }

}
