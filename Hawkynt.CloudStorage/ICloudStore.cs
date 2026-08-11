namespace Hawkynt.CloudStorage;

/// <summary>One entry in a folder listing: a file or a sub-folder.</summary>
public readonly record struct CloudEntry(string Name, bool IsFolder, long Length, DateTime ModifiedUtc);

/// <summary>File or folder metadata; the <see cref="IsFolder"/> flag disambiguates the two.</summary>
public sealed record CloudMeta(bool IsFolder, long Length, DateTime CreatedUtc, DateTime ModifiedUtc);

/// <summary>
/// What a provider can do NATIVELY, as opposed to what the interface's fallbacks emulate.
/// Every operation works on every store; these flags say whether it is cheap.
/// </summary>
[Flags]
public enum CloudCaps {
  None = 0,

  /// <summary>
  /// The provider can serve a byte range without transferring the whole object (an HTTP Range
  /// header, an SDK range option, an FTP restart offset, an SFTP seek). Without it a caller
  /// asking for 128 KiB out of a 4 GiB object moves all 4 GiB.
  /// </summary>
  RangeRead = 1 << 0,

  /// <summary>
  /// The provider can consume an upload from a Stream, so a large file is sent in chunks
  /// instead of being materialised in memory first.
  /// </summary>
  StreamingUpload = 1 << 1,

  /// <summary>The provider can hand back a readable Stream rather than a completed byte[].</summary>
  StreamingDownload = 1 << 2,
}

/// <summary>
/// The provider-neutral contract every remote endpoint offers: object transfer plus namespace
/// primitives. Each provider implements this directly against its official SDK or REST surface —
/// no meta/wrapper libraries. Paths are slash-separated and never rooted (no leading slash); the
/// empty string is the store root. Implementations translate their native failures into
/// <see cref="CloudStorageException"/> so callers depend on one error model.
///
/// The streaming and range members carry WORKING default implementations in terms of the
/// whole-object ones, so a provider is correct the moment it implements <see cref="Download"/>
/// and <see cref="Upload(string, byte[])"/>. What a provider gains by overriding them is
/// declared in <see cref="Caps"/> — callers use that to decide between a cheap range request and
/// a strategy that assumes whole-object transfers (caching an object once rather than per block).
/// </summary>
public interface ICloudStore : IDisposable {

  /// <summary>What this provider does natively; the rest is emulated by the defaults below.</summary>
  CloudCaps Caps => CloudCaps.None;

  /// <summary>Establishes the connection when the protocol needs one; idempotent.</summary>
  void Connect();

  /// <summary>Cheap reachability check for an online probe; never throws.</summary>
  bool Probe();

  /// <summary>Downloads the whole object; throws <see cref="CloudStorageException"/> with <see cref="CloudStorageError.NotFound"/> when absent.</summary>
  byte[] Download(string path);

  /// <summary>Uploads the whole object, overwriting; parent folders are guaranteed to exist beforehand.</summary>
  void Upload(string path, byte[] content);

  /// <summary>
  /// Opens the object for reading as a stream. The default materialises the whole object first;
  /// a provider declaring <see cref="CloudCaps.StreamingDownload"/> streams it.
  /// </summary>
  Stream OpenRead(string path) => new MemoryStream(this.Download(path), false);

  /// <summary>
  /// Reads <paramref name="count"/> bytes from <paramref name="offset"/>. A provider declaring
  /// <see cref="CloudCaps.RangeRead"/> asks the service for exactly that range; the default has
  /// to fetch the whole object and slice it, which is why the capability matters to callers.
  ///
  /// A range that starts at or past the end yields an empty stream; one that overruns the end is
  /// truncated to what exists — the same contract as reading a local file.
  /// </summary>
  Stream OpenReadRange(string path, long offset, long count) {
    var whole = this.Download(path);
    if (offset >= whole.LongLength || count <= 0)
      return new MemoryStream([], false);

    var available = (int)Math.Min(count, whole.LongLength - offset);
    return new MemoryStream(whole, (int)offset, available, false);
  }

  /// <summary>
  /// Uploads from a stream, overwriting. The default buffers it into a single array, which caps
  /// the object at <see cref="int.MaxValue"/> and spikes memory by its whole size; a provider
  /// declaring <see cref="CloudCaps.StreamingUpload"/> sends it in chunks and does neither.
  /// </summary>
  /// <param name="length">The content length when the caller knows it, else negative.</param>
  void Upload(string path, Stream content, long length = -1) {
    using var buffer = length is > 0 and <= int.MaxValue ? new MemoryStream((int)length) : new MemoryStream();
    content.CopyTo(buffer);
    this.Upload(path, buffer.ToArray());
  }

  void DeleteFile(string path);

  /// <summary>File or folder metadata, <see langword="null"/> when nothing exists at the path.</summary>
  CloudMeta? Stat(string path);

  /// <summary>Creates one folder level; parents are guaranteed to exist.</summary>
  void CreateFolder(string path);

  void DeleteFolder(string path);

  IEnumerable<CloudEntry> List(string folder);

}
