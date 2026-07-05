namespace Hawkynt.CloudStorage;

/// <summary>
/// Slash-separated path arithmetic for cloud stores: paths never carry a leading slash and
/// the empty string is the root. Mirrors the semantics the pool engine expects so adapters
/// need no re-mapping.
/// </summary>
public static class CloudPath {

  /// <summary>The last segment of a path ("a/b/c" -> "c"); the empty string for the root.</summary>
  public static string GetName(string path) {
    var trimmed = path.Trim('/');
    var slash = trimmed.LastIndexOf('/');
    return slash < 0 ? trimmed : trimmed[(slash + 1)..];
  }

  /// <summary>The parent of a path ("a/b/c" -> "a/b"); the empty string when already at the root.</summary>
  public static string GetParent(string path) {
    var trimmed = path.Trim('/');
    var slash = trimmed.LastIndexOf('/');
    return slash < 0 ? "" : trimmed[..slash];
  }

  /// <summary>Joins a parent and a child, skipping empty segments ("", "c" -> "c"; "a/b", "c" -> "a/b/c").</summary>
  public static string Combine(string parent, string child) {
    parent = parent.Trim('/');
    child = child.Trim('/');
    return parent.Length == 0 ? child : child.Length == 0 ? parent : $"{parent}/{child}";
  }

}
