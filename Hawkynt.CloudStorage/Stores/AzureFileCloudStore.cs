using Azure;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Hawkynt.CloudStorage;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>Azure File Storage store over Azure.Storage.Files.Shares — a real directory tree.</summary>
public sealed class AzureFileCloudStore : ICloudStore {

  private readonly ShareClient _share;
  private readonly string _rootPath;

  /// <summary>Azure Files share addressed by a connection string.</summary>
  public AzureFileCloudStore(string connectionString, string share, string rootPath) {
    this._share = new ShareClient(connectionString, share);
    this._rootPath = rootPath;
  }

  private string _Map(string physicalPath) => this._rootPath.Length == 0 ? physicalPath : $"{this._rootPath}/{physicalPath}".Trim('/');

  private ShareDirectoryClient _Directory(string physicalFolder) {
    var mapped = this._Map(physicalFolder);
    return mapped.Length == 0 ? this._share.GetRootDirectoryClient() : this._share.GetDirectoryClient(mapped);
  }

  private ShareFileClient _File(string physicalPath) {
    var mapped = this._Map(physicalPath);
    var slash = mapped.LastIndexOf('/');
    var directory = slash < 0 ? this._share.GetRootDirectoryClient() : this._share.GetDirectoryClient(mapped[..slash]);
    return directory.GetFileClient(mapped[(slash + 1)..]);
  }

  /// <summary>Azure Files caps a single UploadRange at 4 MiB, so a streamed body is sent in chunks of that size.</summary>
  private const int _UPLOAD_CHUNK = 4 * 1024 * 1024;

  public CloudCaps Caps => CloudCaps.RangeRead | CloudCaps.StreamingUpload | CloudCaps.StreamingDownload;

  public void Connect() {
  }

  public bool Probe() {
    try {
      return this._share.Exists().Value;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    using var stream = this.OpenRead(physicalPath);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  public Stream OpenRead(string physicalPath) => this._Download(physicalPath, range: default);

  /// <summary>A real ranged read: only the requested bytes cross the wire.</summary>
  public Stream OpenReadRange(string physicalPath, long offset, long count)
    => count <= 0 ? EmptyStream.Instance : this._Download(physicalPath, new HttpRange(offset, count));

  private Stream _Download(string physicalPath, HttpRange range) {
    try {
      var download = this._File(physicalPath).Download(new ShareFileDownloadOptions { Range = range }).Value;
      return new OwnedStream(download.Content, download, download.ContentLength);
    } catch (RequestFailedException e) when (e.Status == 416) {
      return EmptyStream.Instance; // range starts at or past the end
    } catch (RequestFailedException e) when (e.Status == 404) {
      throw new CloudStorageException(CloudStorageError.NotFound, $"File not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content) {
    var file = this._File(physicalPath);
    file.Create(content.Length);
    if (content.Length > 0)
      file.UploadRange(new HttpRange(0, content.Length), new MemoryStream(content));
  }

  /// <summary>
  /// Streams the body up in 4 MiB ranges — the object never exists in memory as a whole. Azure
  /// Files needs the final size up front, so a caller that cannot state the length is served by
  /// the buffering default instead of guessing and creating a file of the wrong size.
  /// </summary>
  public void Upload(string physicalPath, Stream content, long length = -1) {
    if (length < 0) {
      using var buffer = new MemoryStream();
      content.CopyTo(buffer);
      this.Upload(physicalPath, buffer.ToArray());
      return;
    }

    var file = this._File(physicalPath);
    file.Create(length);
    if (length == 0)
      return;

    var chunk = new byte[(int)Math.Min(_UPLOAD_CHUNK, length)];
    long position = 0;
    while (position < length) {
      var wanted = (int)Math.Min(chunk.Length, length - position);
      var filled = 0;
      while (filled < wanted) {
        var read = content.Read(chunk, filled, wanted - filled);
        if (read <= 0)
          break;

        filled += read;
      }

      if (filled == 0)
        throw new CloudStorageException(CloudStorageError.IoError, $"Upload of '{physicalPath}' ended after {position} of {length} bytes");

      using var window = new MemoryStream(chunk, 0, filled, false);
      file.UploadRange(new HttpRange(position, filled), window);
      position += filled;
    }
  }

  public void DeleteFile(string physicalPath) => this._File(physicalPath).DeleteIfExists();

  public CloudMeta? Stat(string physicalPath) {
    var file = this._File(physicalPath);
    try {
      var properties = file.GetProperties().Value;
      return new(false, properties.ContentLength, properties.SmbProperties.FileCreatedOn?.UtcDateTime ?? DateTime.MinValue, properties.LastModified.UtcDateTime);
    } catch (RequestFailedException e) when (e.Status is 404 or 409) {
      // not a file — maybe a directory
    }

    try {
      var directory = this._Directory(physicalPath);
      var properties = directory.GetProperties().Value;
      return new(true, 0, properties.SmbProperties.FileCreatedOn?.UtcDateTime ?? DateTime.MinValue, properties.LastModified.UtcDateTime);
    } catch (RequestFailedException e) when (e.Status is 404 or 409) {
      return null;
    }
  }

  public void CreateFolder(string physicalPath) => this._Directory(physicalPath).CreateIfNotExists();

  public void DeleteFolder(string physicalPath) => this._Directory(physicalPath).DeleteIfExists();

  public IEnumerable<CloudEntry> List(string physicalFolder) {
    foreach (var item in this._Directory(physicalFolder).GetFilesAndDirectories(new ShareDirectoryGetFilesAndDirectoriesOptions { Traits = ShareFileTraits.Timestamps }))
      yield return new(item.Name, item.IsDirectory, item.FileSize ?? 0, item.Properties?.LastWrittenOn?.UtcDateTime ?? DateTime.MinValue);
  }

  public void Dispose() {
  }

}
