using Hawkynt.CloudStorage;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// Shared key mapping for flat object stores: folders are virtual prefixes plus a
/// zero-byte "path/" marker object so empty folders exist and Stat can distinguish them.
/// </summary>
internal static class ObjectKeys {

  public static string File(string rootPrefix, string physicalPath)
    => rootPrefix.Length == 0 ? physicalPath : $"{rootPrefix}/{physicalPath}";

  public static string FolderMarker(string rootPrefix, string physicalFolder)
    => File(rootPrefix, physicalFolder) + "/";

  public static string ListPrefix(string rootPrefix, string physicalFolder) {
    var baseKey = physicalFolder.Length == 0 ? rootPrefix : File(rootPrefix, physicalFolder);
    return baseKey.Length == 0 ? "" : baseKey + "/";
  }

  public static string NameOf(string key) {
    var trimmed = key.TrimEnd('/');
    return trimmed[(trimmed.LastIndexOf('/') + 1)..];
  }

}
