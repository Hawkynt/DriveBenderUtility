namespace Hawkynt.CloudStorage;

/// <summary>One entry in a folder listing: a file or a sub-folder.</summary>
public readonly record struct CloudEntry(string Name, bool IsFolder, long Length, DateTime ModifiedUtc);

/// <summary>File or folder metadata; the <see cref="IsFolder"/> flag disambiguates the two.</summary>
public sealed record CloudMeta(bool IsFolder, long Length, DateTime CreatedUtc, DateTime ModifiedUtc);

/// <summary>
/// The provider-neutral contract every remote endpoint offers: whole-object
/// download/upload plus namespace primitives. Each provider implements this directly
/// against its official SDK or REST surface — no meta/wrapper libraries. Paths are
/// slash-separated and never rooted (no leading slash); the empty string is the store
/// root. Implementations translate their native failures into
/// <see cref="CloudStorageException"/> so callers depend on one error model.
/// </summary>
public interface ICloudStore : IDisposable {

  /// <summary>Establishes the connection when the protocol needs one; idempotent.</summary>
  void Connect();

  /// <summary>Cheap reachability check for an online probe; never throws.</summary>
  bool Probe();

  /// <summary>Downloads the whole object; throws <see cref="CloudStorageException"/> with <see cref="CloudStorageError.NotFound"/> when absent.</summary>
  byte[] Download(string path);

  /// <summary>Uploads the whole object, overwriting; parent folders are guaranteed to exist beforehand.</summary>
  void Upload(string path, byte[] content);

  void DeleteFile(string path);

  /// <summary>File or folder metadata, <see langword="null"/> when nothing exists at the path.</summary>
  CloudMeta? Stat(string path);

  /// <summary>Creates one folder level; parents are guaranteed to exist.</summary>
  void CreateFolder(string path);

  void DeleteFolder(string path);

  IEnumerable<CloudEntry> List(string folder);

}
